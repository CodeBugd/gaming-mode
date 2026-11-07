using System.ServiceProcess;
using GamingMode.Core;

namespace GamingMode.App.Service;

/// <summary>The "AceBotGamingMode" Windows service: does all the admin work for the tray app.</summary>
internal sealed class GamingService : ServiceBase
{
    public const string Name = "AceBotGamingMode";
    private GamingEngine? _engine;
    private PipeServer? _server;
    private Updater? _updater;

    public GamingService()
    {
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args) => Start();

    public void Start()
    {
        Log.Write($"Service starting (v{typeof(GamingService).Assembly.GetName().Version}).");
        _engine = new GamingEngine();
        _engine.StartupCleanup();
        _updater = new Updater(() => _engine.IsOn);
        _server = new PipeServer(_engine, _updater);
        _server.Start();
        TrayLauncher.RelaunchIfUpdated();
        _updater.StartSchedule(() => _engine.AutoUpdateEnabled);
    }

    protected override void OnStop() => Shutdown("Service stopping");
    protected override void OnShutdown() => Shutdown("Windows shutting down");

    public void Shutdown(string why)
    {
        Log.Write($"{why} - turning Gaming Mode off if it's on.");
        _server?.Stop();
        _updater?.Dispose();
        _engine?.Dispose();
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        // Signing out while Gaming Mode is on: put everything back.
        if (change.Reason == SessionChangeReason.SessionLogoff && _engine?.ActiveSessionId == change.SessionId)
            _engine.TurnOff();
    }
}
