using System.Diagnostics;
using GamingMode.Core;
using Xunit.Abstractions;

namespace GamingMode.Tests;

/// <summary>Runs the real Windows calls against a throwaway process the test owns.</summary>
public class NativePriorityTests
{
    private readonly ITestOutputHelper _out;
    public NativePriorityTests(ITestOutputHelper output) => _out = output;

    private static Process StartDummy() => Process.Start(new ProcessStartInfo(
        Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;

    [Fact]
    public void Gpu_priority_can_be_lowered_and_restored_on_a_process_that_uses_the_gpu()
    {
        // Only processes with a GPU context have a GPU priority. Borrow one of this user's (e.g. a browser's GPU process)
        // for a moment; it's put straight back. Skips quietly on a machine with none running.
        int session = Process.GetCurrentProcess().SessionId;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var target = Process.GetProcesses().Where(p => p.SessionId == session)
            .Where(p => Native.GetImagePath(p.Id) is string path && !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(p => Native.GetGpuPriority(p.Id) == 2);
        if (target == null) { _out.WriteLine("No GPU-using process found - skipped."); return; }

        Assert.True(Native.SetGpuPriority(target.Id, Native.GpuPriorityBelowNormal));
        try { Assert.Equal(Native.GpuPriorityBelowNormal, Native.GetGpuPriority(target.Id)); }
        finally { ProcessThrottler.RestoreGpuAndMemory(target.Id, 2, null); }
        Assert.Equal(2, Native.GetGpuPriority(target.Id));
        _out.WriteLine($"Tested on {target.ProcessName} ({target.Id}).");
    }

    [Fact]
    public void Gpu_priority_is_simply_unavailable_for_apps_without_gpu_work()
    {
        using var p = StartDummy();
        try { Assert.Null(Native.GetGpuPriority(p.Id)); }
        finally { p.Kill(); }
    }

    [Fact]
    public void Memory_priority_can_be_lowered_and_restored()
    {
        using var p = StartDummy();
        try
        {
            var original = Native.GetMemoryPriority(p.Id);
            Assert.NotNull(original);
            Assert.True(Native.SetMemoryPriority(p.Id, Native.MemoryPriorityVeryLow));
            Assert.Equal(Native.MemoryPriorityVeryLow, Native.GetMemoryPriority(p.Id));
            ProcessThrottler.RestoreGpuAndMemory(p.Id, null, original);
            Assert.Equal(original, Native.GetMemoryPriority(p.Id));
        }
        finally { p.Kill(); }
    }

    [Fact]
    public void Restore_keeps_a_value_the_app_changed_itself()
    {
        using var p = StartDummy();
        try
        {
            Native.SetMemoryPriority(p.Id, 3);                          // "the app" picked its own value meanwhile
            ProcessThrottler.RestoreGpuAndMemory(p.Id, null, 5);
            Assert.Equal(3, Native.GetMemoryPriority(p.Id));
        }
        finally { p.Kill(); }
    }

    [Fact]
    public void Report_what_idle_cpu_priority_does_to_the_others()
    {
        using var p = StartDummy();
        try
        {
            var before = (Native.GetGpuPriority(p.Id), Native.GetMemoryPriority(p.Id));
            p.PriorityClass = ProcessPriorityClass.Idle;
            var after = (Native.GetGpuPriority(p.Id), Native.GetMemoryPriority(p.Id));
            _out.WriteLine($"GPU/memory priority before Idle: {before}, after Idle: {after}");
        }
        finally { p.Kill(); }
    }
}
