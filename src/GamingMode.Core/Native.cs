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

    // GPU scheduling priority (the same mechanism OBS uses to keep its own rendering smooth).
    [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
    [DllImport("gdi32.dll")] private static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr process, out int priority);

    // Memory priority: low-priority pages are the first to leave RAM when it runs short.
    [StructLayout(LayoutKind.Sequential)] private struct MemoryPriorityInformation { public uint MemoryPriority; }
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "SetProcessInformation")]
    private static extern bool SetProcessMemoryInfo(IntPtr handle, int infoClass, ref MemoryPriorityInformation info, int size);
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetProcessInformation")]
    private static extern bool GetProcessMemoryInfo(IntPtr handle, int infoClass, out MemoryPriorityInformation info, int size);
    private const int ProcessMemoryPriority = 0;
    private const uint ProcessQueryInformation = 0x0400;

    public const int GpuPriorityBelowNormal = 1;
    public const int MemoryPriorityVeryLow = 1;

    // Both GPU calls need SET_INFORMATION | QUERY_INFORMATION (less gives "access denied"), and only work on processes
    // that actually use the GPU - others return STATUS_INVALID_PARAMETER, which we treat as "nothing to lower".
    private const uint GpuAccess = ProcessSetInformation | ProcessQueryInformation;

    public static int? GetGpuPriority(int pid)
    {
        int? result = null;
        WithProcess(pid, GpuAccess, h =>
        {
            if (D3DKMTGetProcessSchedulingPriorityClass(h, out var p) != 0) return false;
            result = p; return true;
        });
        return result;
    }

    public static bool SetGpuPriority(int pid, int priority) =>
        WithProcess(pid, GpuAccess, h => D3DKMTSetProcessSchedulingPriorityClass(h, priority) == 0);

    public static int? GetMemoryPriority(int pid)
    {
        int? result = null;
        WithProcess(pid, ProcessQueryLimitedInformation | ProcessQueryInformation, h =>
        {
            if (!GetProcessMemoryInfo(h, ProcessMemoryPriority, out var info, Marshal.SizeOf<MemoryPriorityInformation>())) return false;
            result = (int)info.MemoryPriority; return true;
        });
        return result;
    }

    public static bool SetMemoryPriority(int pid, int priority) => WithProcess(pid, ProcessSetInformation, h =>
    {
        var info = new MemoryPriorityInformation { MemoryPriority = (uint)priority };
        return SetProcessMemoryInfo(h, ProcessMemoryPriority, ref info, Marshal.SizeOf<MemoryPriorityInformation>());
    });

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
