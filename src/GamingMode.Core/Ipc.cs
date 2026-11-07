using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GamingMode.Core;

/// <summary>One JSON line in, one JSON line out, per connection.</summary>
public sealed class IpcRequest
{
    public string Command { get; set; } = "status";   // status | on | off | toggle | foreground | getSettings | setSettings | preview | checkUpdate
    public int ForegroundPid { get; set; }
    public bool Fullscreen { get; set; }
    public Settings? Settings { get; set; }
}

public sealed class IpcResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public EngineStatus? Status { get; set; }
    public Settings? Settings { get; set; }
    public List<PreviewItem>? Preview { get; set; }
    public string? Message { get; set; }
}

public static class Ipc
{
    public const string PipeName = "AceBot.GamingMode";
    public const int MaxMessageBytes = 1_000_000;

    /// <summary>The protocol is one message per line, so messages must never be pretty-printed.</summary>
    public static readonly JsonSerializerOptions Json = new(Settings.Json) { WriteIndented = false };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T? Deserialize<T>(string line) => JsonSerializer.Deserialize<T>(line, Json);

    public static async Task<IpcResponse> SendAsync(IpcRequest request, int timeoutMs = 3000, CancellationToken ct = default)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
        await WriteLineAsync(pipe, Serialize(request), ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(60));   // turning on can take a few seconds (services, WMI)
        var line = await ReadLineAsync(pipe, cts.Token).ConfigureAwait(false)
                   ?? throw new IOException("Gaming Mode service closed the connection.");
        return Deserialize<IpcResponse>(line) ?? new IpcResponse { Ok = false, Error = "Empty reply." };
    }

    public static async Task WriteLineAsync(Stream s, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await s.WriteAsync(bytes, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<string?> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[4096];
        while (true)
        {
            int n = await s.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return buffer.Length > 0 ? Encoding.UTF8.GetString(buffer.ToArray()) : null;
            int nl = Array.IndexOf(one, (byte)'\n', 0, n);
            if (nl >= 0) { buffer.Write(one, 0, nl); return Encoding.UTF8.GetString(buffer.ToArray()); }
            buffer.Write(one, 0, n);
            if (buffer.Length > MaxMessageBytes) throw new IOException("Message too large.");
        }
    }
}
