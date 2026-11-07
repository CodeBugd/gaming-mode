using System.Runtime.InteropServices;
using System.Text;

namespace GamingMode.Core;

/// <summary>Win32 calls the engine needs. Every method fails soft (returns false/null) instead of throwing.</summary>
internal static class Native
{
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessPowerThrottling = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr handle, int infoClass, ref PowerThrottlingState info, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool K32EmptyWorkingSet(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr handle, int flags, StringBuilder name, ref int size);

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);

    private static bool WithProcess(int pid, uint access, Func<IntPtr, bool> action)
    {
        var handle = OpenProcess(access, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return action(handle); } finally { CloseHandle(handle); }
    }

    /// <summary>Windows "Efficiency mode" (EcoQoS). <paramref name="on"/>=false hands control back to Windows.</summary>
    public static bool SetEfficiencyMode(int pid, bool on) => WithProcess(pid, ProcessSetInformation, h =>
    {
        var state = new PowerThrottlingState { Version = 1, ControlMask = on ? 1u : 0u, StateMask = on ? 1u : 0u };
        return SetProcessInformation(h, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
    });

    /// <summary>Hands the process's idle RAM back to Windows; it pages back in on demand.</summary>
    public static bool TrimWorkingSet(int pid) =>
        WithProcess(pid, ProcessSetQuota | ProcessQueryLimitedInformation, K32EmptyWorkingSet);

    public static string? GetImagePath(int pid)
    {
        string? path = null;
        WithProcess(pid, ProcessQueryLimitedInformation, h =>
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return false;
            path = sb.ToString(0, size);
            return true;
        });
        return path;
    }

    public static Guid? GetActivePowerScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); } finally { LocalFree(ptr); }
    }

    public static bool SetActivePowerScheme(Guid scheme) => PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0;
}
