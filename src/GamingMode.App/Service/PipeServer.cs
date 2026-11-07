using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using GamingMode.Core;

namespace GamingMode.App.Service;

/// <summary>
/// Local named pipe the tray app talks to. Only signed-in local users can connect (network access is denied),
/// and every request acts on the session of the process that sent it - read from Windows, not from the request.
/// </summary>
internal sealed class PipeServer
{
    private const int Listeners = 4;
    private readonly GamingEngine _engine;
    private readonly Updater _updater;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();

    public PipeServer(GamingEngine engine, Updater updater) { _engine = engine; _updater = updater; }

    public void Start()
    {
        for (int i = 0; i < Listeners; i++) _loops.Add(Task.Run(() => AcceptLoop(_cts.Token)));
    }

    public void Stop()
    {
        _cts.Cancel();
        try { Task.WaitAll(_loops.ToArray(), TimeSpan.FromSeconds(3)); } catch { }
    }

    private static PipeSecurity CreateSecurity()
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return s;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(Ipc.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, CreateSecurity());
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await Handle(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write($"Pipe error: {ex.Message}"); await Task.Delay(200, CancellationToken.None).ConfigureAwait(false); }
        }
    }

    private async Task Handle(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var line = await Ipc.ReadLineAsync(pipe, timeout.Token).ConfigureAwait(false);
        if (line == null) return;

        IpcResponse response;
        try
        {
            var request = Ipc.Deserialize<IpcRequest>(line) ?? new IpcRequest();
            var (sessionId, sid) = Identify(pipe);
            response = Dispatch(request, sessionId, sid);
        }
        catch (Exception ex)
        {
            Log.Write($"Request failed: {ex.Message}");
            response = new IpcResponse { Ok = false, Error = ex.Message };
        }
        await Ipc.WriteLineAsync(pipe, Ipc.Serialize(response), ct).ConfigureAwait(false);
    }

    private IpcResponse Dispatch(IpcRequest r, int sessionId, string? sid)
    {
        switch (r.Command)
        {
            case "status": return new IpcResponse { Status = _engine.Status() };
            case "on": return new IpcResponse { Status = _engine.TurnOn(sessionId, sid) };
            case "off": return new IpcResponse { Status = _engine.TurnOff() };
            case "toggle": return new IpcResponse { Status = _engine.Toggle(sessionId, sid) };
            case "foreground":
                // Only the session Gaming Mode is running for may say what's in front.
                return new IpcResponse
                {
                    Status = _engine.ActiveSessionId == sessionId ? _engine.ReportForeground(r.ForegroundPid, r.Fullscreen) : _engine.Status(),
                };
            case "getSettings": return new IpcResponse { Settings = _engine.GetSettings() };
            case "setSettings":
                if (r.Settings == null) return new IpcResponse { Ok = false, Error = "No settings sent." };
                _engine.SaveSettings(r.Settings);
                return new IpcResponse { Settings = _engine.GetSettings() };
            case "preview": return new IpcResponse { Preview = _engine.Preview(sessionId, r.Settings) };
            case "checkUpdate":
            {
                var check = _updater.CheckAndInstallAsync(manual: true);
                // Answer within 20 s; if an update is waiting for Gaming Mode to turn off, it keeps going in the background.
                bool done = check.Wait(TimeSpan.FromSeconds(20));
                return new IpcResponse { Message = done ? check.Result : _updater.LastResult, Status = _engine.Status() };
            }
            default: return new IpcResponse { Ok = false, Error = $"Unknown command '{r.Command}'." };
        }
    }

    // ---------------------------------------------------------------- who is asking
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    private static (int SessionId, string? Sid) Identify(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
            throw new InvalidOperationException("Couldn't identify the caller.");
        if (!ProcessIdToSessionId(pid, out var session))
            throw new InvalidOperationException("Couldn't find the caller's session.");

        string? sid = null;
        var process = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                if (OpenProcessToken(process, 0x0008 /*TOKEN_QUERY*/, out var token))
                {
                    try { using var id = new WindowsIdentity(token); sid = id.User?.Value; }
                    finally { CloseHandle(token); }
                }
            }
            finally { CloseHandle(process); }
        }
        return ((int)session, sid);
    }
}
