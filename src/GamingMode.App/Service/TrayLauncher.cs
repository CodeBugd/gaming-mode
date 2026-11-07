using System.Runtime.InteropServices;
using GamingMode.Core;

namespace GamingMode.App.Service;

/// <summary>
/// After a silent update the old tray icon was closed by the installer; the new service starts a fresh one
/// for whoever is signed in at the console, running as that user (not as SYSTEM).
/// </summary>
internal static class TrayLauncher
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out IntPtr token);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll")] private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(IntPtr token, string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? dir, ref StartupInfo si, out ProcessInformation pi);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    public static void RelaunchIfUpdated()
    {
        var flag = Path.Combine(Paths.DataDir, Updater.RelaunchTrayFlag);
        if (!File.Exists(flag)) return;
        try { File.Delete(flag); } catch { }

        uint session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF || !WTSQueryUserToken(session, out var token)) { Log.Write("Updated - nobody signed in, tray starts at next sign-in."); return; }
        IntPtr env = IntPtr.Zero;
        try
        {
            CreateEnvironmentBlock(out env, token, false);
            var exe = Environment.ProcessPath!;
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
            const uint CreateUnicodeEnvironment = 0x400;
            if (CreateProcessAsUserW(token, exe, $"\"{exe}\" --tray", IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment,
                    env, Path.GetDirectoryName(exe), ref si, out var pi))
            {
                CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
                Log.Write($"Updated to v{Updater.Current} - tray icon restarted.");
            }
            else Log.Write($"Updated - couldn't restart the tray icon (error {Marshal.GetLastWin32Error()}); it starts at next sign-in.");
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            CloseHandle(token);
        }
    }
}
