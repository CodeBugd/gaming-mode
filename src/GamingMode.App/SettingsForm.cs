using System.Diagnostics;
using GamingMode.Core;

namespace GamingMode.App;

/// <summary>Settings: what to protect, upload limits, which features run, and a preview of what gets slowed down.</summary>
internal sealed class SettingsForm : Form
{
    private Settings _settings = Settings.CreateDefault();
    private readonly ListBox _protected = new() { Dock = DockStyle.Fill, IntegralHeight = false, Sorted = true };
    private readonly TextBox _newEntry = new() { Dock = DockStyle.Fill, PlaceholderText = "e.g.  myapp   or   path:\\MyGames\\" };
    private readonly DataGridView _limits = new()
    {
        Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, RowHeadersVisible = false,
        AllowUserToAddRows = true, AllowUserToDeleteRows = true, BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None,
    };
    private readonly Dictionary<string, CheckBox> _features = new();
    private readonly ListView _preview = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly Label _previewSummary = new() { Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(0, 6, 0, 0) };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly Button _save = new() { Text = "Save", AutoSize = true, MinimumSize = new Size(90, 30) };

    private static readonly (string Key, string Text)[] FeatureText =
    {
        (nameof(FeatureSet.ThrottleApps), "Push background apps to the back (lowest priority + Efficiency mode). The app you're using always runs at full speed."),
        (nameof(FeatureSet.TrimMemory), "Free up RAM: background apps hand back memory they aren't using."),
        (nameof(FeatureSet.PauseServices), "Pause Windows Search indexing, SysMain and telemetry."),
        (nameof(FeatureSet.PauseWindowsUpdate), "Pause Windows Update (same as the Settings app; expires on its own)."),
        (nameof(FeatureSet.UploadLimits), "Cap the upload speed of the apps on the Upload limits tab, so your stream keeps the bandwidth."),
        (nameof(FeatureSet.HoldMaintenance), "Hold off Windows Automatic Maintenance (disk optimise, scheduled scans)."),
        (nameof(FeatureSet.LimitDefenderScans), "Limit Defender's scheduled scans to 5% CPU. Real-time protection stays fully on."),
        (nameof(FeatureSet.StopBackgroundRecording), "Turn off Game Bar background recording while OBS / Streamlabs / XSplit are open."),
        (nameof(FeatureSet.HighPerformancePower), "Switch to the High performance power plan if you're on Balanced or Power saver."),
        (nameof(FeatureSet.WindowsGameMode), "Make sure Windows Game Mode is on."),
        (nameof(FeatureSet.AutoUpdate), "Install updates automatically (checked twice a day, never while Gaming Mode is on)."),
    };

    public SettingsForm(bool firstRun)
    {
        Text = "Gaming Mode settings";
        Icon = Icons.On;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(820, 620);
        MinimumSize = new Size(640, 480);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        _tabs.TabPages.Add(BuildProtectedTab());
        _tabs.TabPages.Add(BuildLimitsTab());
        _tabs.TabPages.Add(BuildFeaturesTab());
        _tabs.TabPages.Add(BuildPreviewTab(firstRun));

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 48, Padding = new Padding(8) };
        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 30) };
        close.Click += (_, _) => Close();
        _save.Click += async (_, _) => await SaveAsync();
        bottom.Controls.Add(close);
        bottom.Controls.Add(_save);

        Controls.Add(_tabs);
        Controls.Add(bottom);
        AcceptButton = _save;

        if (firstRun) _tabs.SelectedIndex = 3;
        Shown += async (_, _) => { await LoadAsync(); if (firstRun) await RefreshPreviewAsync(); };
    }

    // ---------------------------------------------------------------- tabs
    private TabPage BuildProtectedTab()
    {
        var page = new TabPage("Protected apps") { Padding = new Padding(10) };
        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 48, AutoSize = false,
            Text = "These are never slowed down. Everything else you have open is pushed to the back while Gaming Mode is on.\n" +
                   "Use an app's name (wildcards * allowed) or path: plus part of its folder to cover everything installed there.",
        };
        var add = new Button { Text = "Add", AutoSize = true };
        add.Click += (_, _) => AddEntry(_newEntry.Text);
        _newEntry.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { AddEntry(_newEntry.Text); e.SuppressKeyPress = true; } };
        var addRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 2 };
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        addRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        addRow.Controls.Add(_newEntry, 0, 0);
        addRow.Controls.Add(add, 1, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, Width = 190, Padding = new Padding(8, 0, 0, 0) };
        var running = new Button { Text = "Add a running app...", Width = 175, Height = 32 };
        running.Click += (_, _) => ShowRunningApps(running);
        var remove = new Button { Text = "Remove selected", Width = 175, Height = 32 };
        remove.Click += (_, _) => { foreach (var item in _protected.SelectedItems.Cast<string>().ToList()) _protected.Items.Remove(item); };
        _protected.SelectionMode = SelectionMode.MultiExtended;
        var reset = new Button { Text = "Reset to defaults", Width = 175, Height = 32 };
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Replace your list with the default list?", "Gaming Mode", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            _protected.Items.Clear();
            _protected.Items.AddRange(Settings.DefaultProtectedApps.Cast<object>().ToArray());
        };
        buttons.Controls.AddRange(new Control[] { running, remove, reset });

        page.Controls.Add(_protected);
        page.Controls.Add(buttons);
        page.Controls.Add(addRow);
        page.Controls.Add(intro);
        return page;
    }

    private TabPage BuildLimitsTab()
    {
        var page = new TabPage("Upload limits") { Padding = new Padding(10) };
        var intro = new Label
        {
            Dock = DockStyle.Top, Height = 48, AutoSize = false,
            Text = "While Gaming Mode is on, these apps can only upload this fast, so your stream and game keep the bandwidth.\n" +
                   "Downloads aren't affected. Apps on your protected list are never capped.",
        };
        _limits.Columns.Add(new DataGridViewTextBoxColumn { Name = "App", HeaderText = "App (.exe name)", FillWeight = 70 });
        _limits.Columns.Add(new DataGridViewTextBoxColumn { Name = "Mbps", HeaderText = "Max upload (Mbps)", FillWeight = 30 });
        page.Controls.Add(_limits);
        page.Controls.Add(intro);
        return page;
    }

    private TabPage BuildFeaturesTab()
    {
        var page = new TabPage("Features") { Padding = new Padding(10), AutoScroll = true };
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        list.Controls.Add(new Label { Text = "Choose what Gaming Mode does. Everything is put back when you turn it off.", AutoSize = true, Margin = new Padding(3, 3, 3, 10) });
        foreach (var (key, text) in FeatureText)
        {
            var cb = new CheckBox { Text = text, AutoSize = true, MaximumSize = new Size(740, 0), Margin = new Padding(3, 4, 3, 4) };
            _features[key] = cb;
            list.Controls.Add(cb);
        }
        page.Controls.Add(list);
        return page;
    }

    private TabPage BuildPreviewTab(bool firstRun)
    {
        var page = new TabPage("Preview") { Padding = new Padding(10) };
        var intro = new Label
        {
            Dock = DockStyle.Top, Height = firstRun ? 70 : 44, AutoSize = false,
            Text = (firstRun ? "Welcome! Before you use Gaming Mode, check this list. Anything you need at full speed while gaming " +
                               "(chat, music, stream tools) should say Protected - if not, add it on the Protected apps tab.\n" : "") +
                   "What turning Gaming Mode on would slow down right now (uses your unsaved changes too). Nothing is changed.",
        };
        var check = new Button { Text = "Check now", AutoSize = true, Dock = DockStyle.Top };
        check.Click += async (_, _) => await RefreshPreviewAsync();
        _preview.Columns.Add("App", 180);
        _preview.Columns.Add("While gaming", 150);
        _preview.Columns.Add("RAM", 80, HorizontalAlignment.Right);
        _preview.Columns.Add("Location", 360);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Protect this app", null, (_, _) =>
        {
            foreach (ListViewItem item in _preview.SelectedItems) AddEntry(item.Text);
            _ = RefreshPreviewAsync();
        });
        _preview.ContextMenuStrip = menu;
        page.Controls.Add(_preview);
        page.Controls.Add(_previewSummary);
        page.Controls.Add(check);
        page.Controls.Add(intro);
        return page;
    }

    // ---------------------------------------------------------------- data
    private async Task LoadAsync()
    {
        try
        {
            var r = await Ipc.SendAsync(new IpcRequest { Command = "getSettings" });
            if (r.Settings != null) _settings = r.Settings;
        }
        catch (Exception ex)
        {
            _save.Enabled = false;
            MessageBox.Show(this, $"Couldn't reach the Gaming Mode service, so settings can't be loaded or saved.\n\n{ex.Message}", "Gaming Mode",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _protected.Items.Clear();
        _protected.Items.AddRange(_settings.ProtectedApps.Cast<object>().ToArray());
        _limits.Rows.Clear();
        foreach (var l in _settings.UploadLimits) _limits.Rows.Add(l.App, l.Mbps.ToString("0.##"));
        foreach (var (key, cb) in _features) cb.Checked = (bool)typeof(FeatureSet).GetProperty(key)!.GetValue(_settings.Features)!;
    }

    private Settings Collect()
    {
        var s = new Settings
        {
            ProtectedApps = _protected.Items.Cast<string>().ToList(),
            ScanSeconds = _settings.ScanSeconds, GraceSeconds = _settings.GraceSeconds,
            DefenderScanCpuPercent = _settings.DefenderScanCpuPercent, WindowsUpdatePauseDays = _settings.WindowsUpdatePauseDays,
        };
        foreach (DataGridViewRow row in _limits.Rows)
        {
            if (row.IsNewRow) continue;
            var app = row.Cells[0].Value?.ToString()?.Trim() ?? "";
            if (app.Length == 0) continue;
            if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app += ".exe";
            if (double.TryParse(row.Cells[1].Value?.ToString(), out var mbps) && mbps > 0) s.UploadLimits.Add(new UploadLimit { App = app, Mbps = mbps });
        }
        foreach (var (key, cb) in _features) typeof(FeatureSet).GetProperty(key)!.SetValue(s.Features, cb.Checked);
        return s.Normalize();
    }

    private async Task SaveAsync()
    {
        _save.Enabled = false;
        try
        {
            var r = await Ipc.SendAsync(new IpcRequest { Command = "setSettings", Settings = Collect() });
            if (!r.Ok) throw new InvalidOperationException(r.Error);
            if (r.Settings != null) _settings = r.Settings;
            Text = "Gaming Mode settings - saved";
        }
        catch (Exception ex) { MessageBox.Show(this, $"Couldn't save: {ex.Message}", "Gaming Mode", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { _save.Enabled = true; }
    }

    private async Task RefreshPreviewAsync()
    {
        _previewSummary.Text = "Checking...";
        try
        {
            var r = await Ipc.SendAsync(new IpcRequest { Command = "preview", Settings = Collect() }, 3000);
            var items = r.Preview ?? new List<PreviewItem>();
            _preview.BeginUpdate();
            _preview.Items.Clear();
            foreach (var g in items.GroupBy(i => (i.Name, i.Protected)))
            {
                var first = g.First();
                var item = new ListViewItem(new[]
                {
                    first.Name, first.Protected ? "Protected" : "Slowed down",
                    $"{g.Sum(i => i.WorkingSetBytes) / 1048576.0:0} MB", first.Path ?? "",
                }) { ForeColor = first.Protected ? Color.SeaGreen : Color.DarkOrange };
                _preview.Items.Add(item);
            }
            _preview.EndUpdate();
            int slowed = items.Where(i => !i.Protected).Select(i => i.Name).Distinct().Count();
            int kept = items.Where(i => i.Protected).Select(i => i.Name).Distinct().Count();
            _previewSummary.Text = $"{slowed} apps would be slowed down, {kept} protected. Windows' own processes are never touched. Right-click an app to protect it.";
        }
        catch (Exception ex) { _previewSummary.Text = $"Couldn't check: {ex.Message}"; }
    }

    private void AddEntry(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (!_protected.Items.Cast<string>().Any(i => i.Equals(text, StringComparison.OrdinalIgnoreCase))) _protected.Items.Add(text);
        _newEntry.Clear();
    }

    private void ShowRunningApps(Control anchor)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        int session = Process.GetCurrentProcess().SessionId;
        var have = _protected.Items.Cast<string>().ToList();
        var protection = new Protection(have);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session) continue;
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path == null || path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!protection.IsProtected(p.ProcessName, path)) names.Add(p.ProcessName);
                }
                catch { }
            }
        }
        var menu = new ContextMenuStrip();
        if (names.Count == 0) menu.Items.Add(new ToolStripMenuItem("Everything running is already protected") { Enabled = false });
        foreach (var n in names) menu.Items.Add(n, null, (_, _) => AddEntry(n));
        menu.Show(anchor, new Point(0, anchor.Height));
    }
}
