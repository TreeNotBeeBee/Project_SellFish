using System.IO.Pipes;
using System.Text.Json;
using WindowPilot;

// The browser owns stdin/stdout. Keep even diagnostic text off stdout.
if (args.Length == 0 || args[0] != BrowserWire.Origin) return 1;
using var pipe = new NamedPipeClientStream(".", BrowserWire.PipeName, PipeDirection.InOut,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
try
{
    // Read the first frame before connecting. This allows a useful rejection when
    // no panel is awaiting pairing, without retaining tab content in a log.
    byte[]? hello = await BrowserWire.ReadAsync(input, CancellationToken.None);
    if (hello == null) return 0;
    using var pair = JsonDocument.Parse(hello);
    string? session = pair.RootElement.GetProperty("session").GetString();
    if (pair.RootElement.GetProperty("type").GetString() != "pair" || session is not { Length: > 0 and <= 128 }) return 1;
    try { await pipe.ConnectAsync(2000); }
    catch (TimeoutException)
    {
        byte[] reject = JsonSerializer.SerializeToUtf8Bytes(new { type = "reject", session, message = "请先在 sell fish 绑定浏览器窗口，然后点击「连接网页」。" }, BrowserWire.Json);
        await BrowserWire.WriteAsync(output, reject, CancellationToken.None);
        return 0;
    }
    if (!BrowserWire.PeerIs(pipe, "sell fish.exe")) return 1;
    await BrowserWire.WriteAsync(pipe, hello, CancellationToken.None);
    using var stop = new CancellationTokenSource();
    Task fromPage = Task.Run(() => Forward(input, pipe, stop.Token));
    Task toPage = Task.Run(() => Forward(pipe, output, stop.Token));
    await await Task.WhenAny(fromPage, toPage);
    stop.Cancel();
    return 0;
}
catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or
    InvalidOperationException or KeyNotFoundException or OperationCanceledException)
{
    return 1;
}

static async Task Forward(Stream from, Stream to, CancellationToken token)
{
    while (await BrowserWire.ReadAsync(from, token) is byte[] payload)
        await BrowserWire.WriteAsync(to, payload, token);
}
