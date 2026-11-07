using System.ServiceProcess;
using GamingMode.App.Service;
using GamingMode.Core;

namespace GamingMode.App;

/// <summary>
///   GamingMode.exe [--tray]        tray icon (started at sign-in, and by the service after an update)
///   GamingMode.exe --toggle        turn Gaming Mode on/off (desktop shortcut), then keep a tray icon
///   GamingMode.exe --settings      settings window
///   GamingMode.exe --service       run as the Windows service (started by Windows, not by you)
///   GamingMode.exe --console       run the service in a console window for debugging (admin)
///   GamingMode.exe --close-tray    ask every tray icon to close (used by the installer)
/// </summary>
internal static class Program
{
    private const string TrayMutexName = @"Local\AceBot.GamingMode.Tray";

    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
        switch (mode)
        {
            case "--service":
                ServiceBase.Run(new GamingService());
                return 0;
            case "--console":
                return RunConsole();
            case "--close-tray":
                return CloseTrays();
        }

        ApplicationConfiguration.Initialize();
        if (mode == "--settings")
        {
            Application.Run(new SettingsForm(firstRun: false));
            return 0;
        }

        EngineStatus? changed = null;
        if (mode == "--toggle")
        {
            try
            {
                var r = Ipc.SendAsync(new IpcRequest { Command = "toggle" }, 3000).GetAwaiter().GetResult();
                if (!r.Ok) { MessageBox.Show(r.Error, "Gaming Mode", MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
                changed = r.Status;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"The Gaming Mode service isn't running, so Gaming Mode can't be switched.\n\n{ex.Message}\n\nReinstalling Gaming Mode fixes this.",
                    "Gaming Mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        // One tray icon per user. If it's already there it has noticed the change and shows the notification.
        using var mutex = new Mutex(true, TrayMutexName, out bool first);
        if (!first) return 0;
        Application.Run(new TrayApp(changed));
        return 0;
    }

    private static int RunConsole()
    {
        AllocConsoleIfNeeded();
        var svc = new GamingService();
        svc.Start();
        Console.WriteLine("Gaming Mode service running in console mode. Press Enter to stop.");
        Console.ReadLine();
        svc.Shutdown("Console stopped");
        return 0;
    }

    private static int CloseTrays()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, TrayApp.TrayExitEventName);
            ev.Set();
            Thread.Sleep(2500);   // trays check once a second
            ev.Reset();
        }
        catch { }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    private static void AllocConsoleIfNeeded() { if (GetConsoleWindow() == IntPtr.Zero) AllocConsole(); }
}
