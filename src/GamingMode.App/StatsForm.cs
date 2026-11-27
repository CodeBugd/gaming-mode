using GamingMode.Core;
using Timer = System.Windows.Forms.Timer;

namespace GamingMode.App;

/// <summary>Live view while Gaming Mode is on: what's pushed back, what it's using, and what was changed. Refreshes every 2 s.</summary>
internal sealed class StatsForm : Form
{
    private readonly Label _headline = new() { Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI Semibold", 13f), AutoSize = false };
    private readonly Label _numbers = new() { Dock = DockStyle.Top, Height = 48, AutoSize = false };
    private readonly ListView _apps = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly Label _changes = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 170, Padding = new Padding(0, 8, 0, 0) };
    private readonly Button _toggle = new() { Text = "Turn on", AutoSize = true, MinimumSize = new Size(110, 30) };
    private readonly Timer _timer = new() { Interval = 2000 };
    private bool _busy;

    public StatsForm()
    {
        Text = "Gaming Mode - live stats";
        Icon = Icons.On;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(640, 600);
        MinimumSize = new Size(480, 420);
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Padding = new Padding(12);

        _apps.Columns.Add("Pushed-back app", 200);
        _apps.Columns.Add("CPU now", 80, HorizontalAlignment.Right);
        _apps.Columns.Add("CPU used since", 110, HorizontalAlignment.Right);
        _apps.Columns.Add("RAM", 80, HorizontalAlignment.Right);
        _apps.Columns.Add("Processes", 80, HorizontalAlignment.Right);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(0, 6, 0, 0) };
        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 30) };
        close.Click += (_, _) => Close();
        _toggle.Click += async (_, _) => await ToggleAsync();
        bottom.Controls.Add(close);
        bottom.Controls.Add(_toggle);

        Controls.Add(_apps);
        Controls.Add(_changes);
        Controls.Add(bottom);
        Controls.Add(_numbers);
        Controls.Add(_headline);
        CancelButton = close;

        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        FormClosed += (_, _) => _timer.Dispose();
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var r = await Ipc.SendAsync(new IpcRequest { Command = "stats" }, 1500);
            if (r.Stats == null) throw new InvalidOperationException(r.Error ?? "The service didn't send stats (it may need updating).");
            Render(r.Stats);
        }
        catch (Exception ex)
        {
            _headline.Text = "Can't reach the Gaming Mode service";
            _numbers.Text = ex.Message;
            _toggle.Enabled = false;
        }
        finally { _busy = false; }
    }

    private void Render(EngineStats s)
    {
        _toggle.Enabled = true;
        _toggle.Text = s.IsOn ? "Turn off" : "Turn on";
        if (!s.IsOn)
        {
            _headline.Text = "Gaming Mode is off";
            _numbers.Text = string.IsNullOrEmpty(s.LastReport) ? "Turn it on to see what gets pushed back." : $"Last time: {s.LastReport}";
            _apps.Items.Clear();
            _changes.Text = "";
            return;
        }

        var on = s.EnabledAtUtc is DateTime t ? DateTime.UtcNow - t : TimeSpan.Zero;
        _headline.Text = $"ON for {FormatDuration(on)}{(s.AutoStarted ? " - turned on by a game" : "")}";
        _numbers.Text =
            $"{s.Apps.Count} apps pushed back, using {s.BackgroundCpuNow:0.0}% of your CPU right now " +
            $"({s.BackgroundCpuSession:0.0}% on average this session). {s.FreedMb:0} MB of RAM handed back when they were pushed back." +
            (s.FullSpeed.Count > 0 ? $"\nLeft at full speed (full screen): {string.Join(", ", s.FullSpeed)}" : "");

        _apps.BeginUpdate();
        int top = _apps.TopItem?.Index ?? 0;
        _apps.Items.Clear();
        foreach (var a in s.Apps)
            _apps.Items.Add(new ListViewItem(new[]
            {
                a.Name, $"{a.CpuPercent:0.0}%", $"{a.CpuSeconds:0.#} s", $"{a.RamMb:0} MB", a.Processes.ToString(),
            }));
        if (_apps.Items.Count > 0) _apps.TopItem = _apps.Items[Math.Min(top, _apps.Items.Count - 1)];
        _apps.EndUpdate();

        _changes.Text = s.Changes.Count == 0
            ? "No system settings needed changing (they were already the way Gaming Mode wants them, or those features are off)."
            : "Changed until Gaming Mode turns off:\n" + string.Join("\n", s.Changes.Select(c => "•  " + c));
    }

    private async Task ToggleAsync()
    {
        _toggle.Enabled = false;
        try { await Ipc.SendAsync(new IpcRequest { Command = "toggle" }, 3000); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Gaming Mode", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        await RefreshAsync();
    }

    internal static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{Math.Max(0, t.Seconds)} s";
}
