using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WindowPilot;

internal sealed record BrowserBlock(string Kind, string Text);
internal sealed record BrowserState(string Conversation, string Draft, IReadOnlyList<BrowserBlock> Blocks, bool CanSend, bool Generating);
internal sealed record BrowserReply(bool Ok, string Message);

/// <summary>One user-armed browser session for the controller's currently bound HWND.</summary>
internal sealed class BrowserBroker(Action<Action> post) : IDisposable
{
    private sealed class Pairing(WindowTarget target)
    {
        internal readonly WindowTarget Target = target;
        internal readonly CancellationTokenSource Lifetime = new();
        internal NamedPipeServerStream? Pipe;
        internal string? Session;
        internal bool Paused;
        internal string? Conversation;
        internal long MayPromoteUntil;
        internal Pending? Command;
    }

    private sealed record Pending(string Kind, string Id, TaskCompletionSource<BrowserReply> Response);
    private const string Idle = "请先绑定 Edge 或 Chrome 窗口，再连接网页。";
    private readonly object _gate = new();
    private readonly Func<WindowTarget, bool> _foregroundMatches = ForegroundMatches;
    // The transport smoke test supplies the foreground check on a noninteractive desktop.
    internal BrowserBroker(Action<Action> post, Func<WindowTarget, bool> foregroundMatches) : this(post) =>
        _foregroundMatches = foregroundMatches;
    private Pairing? _current;
    private bool _disposed;
    private string _status = Idle;
    private BrowserState? _state;

    internal event Action? Changed;
    internal string Status { get { lock (_gate) return _status; } }
    internal BrowserState? State { get { lock (_gate) return _state; } }
    internal bool Connected { get { lock (_gate) return _current is { Session: not null, Paused: false }; } }
    private void Notify() => post(() => { if (!_disposed) Changed?.Invoke(); });

    internal void BeginPairing(WindowTarget target)
    {
        if (BrowserFor(target.ProcessName) == null)
            throw new InvalidOperationException("请先在基础页选择 Edge 或 Chrome 的窗口。");
        var pairing = new Pairing(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EndLocked();
            _current = pairing;
            _status = "请在已绑定的浏览器窗口打开 ChatGPT，点击 sell fish 扩展图标（两分钟内有效）。";
        }
        Notify();
        _ = Task.Run(() => ListenAsync(pairing));
    }

    internal void Disconnect()
    {
        lock (_gate) { EndLocked(); _status = Idle; }
        Notify();
    }

    private void EndLocked()
    {
        if (_current is { } pairing)
        {
            _current = null;
            pairing.Lifetime.Cancel();
            pairing.Pipe?.Dispose();
            pairing.Command?.Response.TrySetResult(new(false, "连接已结束，请检查原网页。"));
        }
        _state = null;
    }

    internal async Task<BrowserReply> CommandAsync(string type, string expected, string text)
    {
        if (type is not ("draft" or "send" or "stop")) return new(false, "未知的网页操作。");
        if (Encoding.UTF8.GetByteCount(text) > 65536 || Encoding.UTF8.GetByteCount(expected) > 65536)
            return new(false, "草稿过长，请在原网页编辑。");
        Pairing pairing;
        Pending command;
        NamedPipeServerStream pipe;
        lock (_gate)
        {
            if (_current is not { Session: not null, Pipe: not null, Paused: false } active || _state == null)
                return new(false, "网页未连接，草稿仍可在浮窗内编辑。");
            if (active.Command != null) return new(false, "请等待上一个网页操作完成。");
            if (type != "stop" && _state.Draft != expected && _state.Draft != text)
                return new(false, "原网页的草稿已更改，请核对后再操作。");
            pairing = active;
            pipe = active.Pipe;
            command = new(type, Guid.NewGuid().ToString("N"), new(TaskCreationOptions.RunContinuationsAsynchronously));
            active.Command = command;
            if (type == "send" && active.Conversation == "new")
                active.MayPromoteUntil = Environment.TickCount64 + 60_000;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(pairing.Lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // A single outstanding command serializes frame writes and binds each reply to its ID.
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { type, session = pairing.Session, id = command.Id, expected, text }, BrowserWire.Json);
            await BrowserWire.WriteAsync(pipe, bytes, timeout.Token).ConfigureAwait(false);
            return await command.Response.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            const string detail = "网页未确认本次操作。请核对原对话，再重新连接。";
            lock (_gate)
            {
                if (_current == pairing) { PauseLocked(pairing, detail); pairing.Pipe?.Dispose(); }
            }
            Notify();
            return new(false, detail);
        }
        finally { lock (_gate) { if (pairing.Command == command) pairing.Command = null; } }
    }

    private async Task ListenAsync(Pairing pairing)
    {
        string finished = "浏览器连接已结束。请检查原网页并重新连接。";
        try
        {
            using var arming = CancellationTokenSource.CreateLinkedTokenSource(pairing.Lifetime.Token);
            arming.CancelAfter(TimeSpan.FromMinutes(2));
            while (true)
            {
                NamedPipeServerStream pipe;
                lock (_gate)
                {
                    if (_current != pairing) return;
                    pipe = new(BrowserWire.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
                    pairing.Pipe = pipe;
                }
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync(arming.Token).ConfigureAwait(false);
                    if (!BrowserWire.PeerIs(pipe, BrowserWire.HostExe)) continue;
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(arming.Token);
                    handshake.CancelAfter(TimeSpan.FromSeconds(5));
                    bool accepted;
                    try { accepted = await AcceptAsync(pairing, pipe, handshake.Token).ConfigureAwait(false); }
                    catch (Exception ex) when (!arming.IsCancellationRequested &&
                        ex is IOException or JsonException or InvalidDataException or OperationCanceledException) { continue; }
                    if (!accepted) continue;
                    while (await BrowserWire.ReadAsync(pipe, pairing.Lifetime.Token).ConfigureAwait(false) is byte[] incoming)
                    {
                        using var document = JsonDocument.Parse(incoming);
                        lock (_gate)
                        {
                            if (_current != pairing) return;
                            ReceiveLocked(pairing, document.RootElement);
                        }
                        Notify();
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { finished = "连接等待已结束。点击「连接网页」可再试一次。"; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { finished = "浏览器连接不可用，请检查扩展与本地连接器后重新连接。"; }
        finally
        {
            lock (_gate)
            {
                if (_current == pairing)
                {
                    if (!pairing.Paused) PauseLocked(pairing, finished);
                    pairing.Session = null;
                    pairing.Pipe = null;
                }
            }
            Notify();
        }
    }

    private async Task<bool> AcceptAsync(Pairing pairing, NamedPipeServerStream pipe, CancellationToken token)
    {
        if (await BrowserWire.ReadAsync(pipe, token).ConfigureAwait(false) is not byte[] payload) return false;
        using var document = JsonDocument.Parse(payload);
        var hello = document.RootElement;
        string session = Field(hello, "session", 128);
        string documentId = Field(hello, "documentId", 128);
        if (Field(hello, "type", 16) != "pair" || session.Length == 0 || documentId.Length == 0 ||
            !hello.TryGetProperty("windowId", out var window) || !window.TryGetInt32(out int windowId) || windowId < 0 ||
            !hello.TryGetProperty("tabId", out var tab) || !tab.TryGetInt32(out int tabId) || tabId < 0)
            throw new InvalidDataException();
        bool match = Field(hello, "browser", 16) == BrowserFor(pairing.Target.ProcessName) && _foregroundMatches(pairing.Target);
        string message = match ? "" : "当前网页与绑定窗口不一致。请在已绑定窗口点击扩展图标。";
        await BrowserWire.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new { type = match ? "accept" : "reject", session, message }, BrowserWire.Json), token).ConfigureAwait(false);
        lock (_gate)
        {
            if (_current != pairing) return false;
            if (match) pairing.Session = session;
            _status = match ? "网页已配对，正在读取对话。" : message;
        }
        Notify();
        return match;
    }

    private void ReceiveLocked(Pairing pairing, JsonElement frame)
    {
        if (Field(frame, "session", 128) != pairing.Session) throw new InvalidDataException();
        switch (Field(frame, "type", 16))
        {
            case "state":
                if (pairing.Paused) return;
                string conversation = Field(frame, "conversation", 256);
                if (conversation != "new" && (!conversation.StartsWith("c/", StringComparison.Ordinal) || conversation.Length <= 2))
                    throw new InvalidDataException();
                if (pairing.Conversation is string bound && bound != conversation &&
                    !(bound == "new" && conversation != "new" && Environment.TickCount64 < pairing.MayPromoteUntil))
                {
                    PauseLocked(pairing, "原网页已切换对话，请重新连接。");
                    return;
                }
                string draft = Field(frame, "draft", 65536);
                if (!frame.TryGetProperty("blocks", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 4096)
                    throw new InvalidDataException();
                var blocks = new List<BrowserBlock>(items.GetArrayLength());
                foreach (var item in items.EnumerateArray())
                {
                    string kind = Field(item, "kind", 16);
                    if (kind is not ("code" or "paragraph" or "list")) throw new InvalidDataException();
                    blocks.Add(new(kind, Field(item, "text", BrowserWire.MaxBytes)));
                }
                pairing.Conversation = conversation;
                _state = new(conversation, draft, blocks, Flag(frame, "canSend"), Flag(frame, "generating"));
                _status = "已连接到选定的 ChatGPT 对话。";
                break;
            case "paused":
                PauseLocked(pairing, Field(frame, "message", 1024));
                break;
            case "result":
                string id = Field(frame, "id", 128);
                bool ok = Flag(frame, "ok");
                string message = Field(frame, "message", 1024);
                if (pairing.Command is { } command && id == command.Id)
                {
                    if (!ok && command.Kind == "send") pairing.MayPromoteUntil = 0;
                    command.Response.TrySetResult(new(ok, message));
                }
                break;
            default: throw new InvalidDataException();
        }
    }

    private void PauseLocked(Pairing pairing, string message)
    {
        pairing.Paused = true;
        _status = message;
        pairing.Command?.Response.TrySetResult(new(false, message));
    }

    private static string Field(JsonElement obj, string name, int max)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var field) ||
            field.ValueKind != JsonValueKind.String || field.GetString() is not string value || value.Length > max)
            throw new InvalidDataException();
        return value;
    }
    private static bool Flag(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? f.GetBoolean() : throw new InvalidDataException();

    private static string? BrowserFor(string name) => name.ToLowerInvariant() switch
    { "msedge" => "edge", "chrome" => "chrome", _ => null };

    private static bool ForegroundMatches(WindowTarget target)
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0 || (foreground != target.Handle && GetAncestor(foreground, 3 /* GA_ROOTOWNER */) != target.Handle))
            return false;
        if (GetWindowThreadProcessId(target.Handle, out var pid) != target.ThreadId || pid != target.ProcessId) return false;
        try
        {
            using var browser = Process.GetProcessById((int)pid);
            return browser.ProcessName.Equals(target.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                (target.ProcessStartTicks == 0 || browser.StartTime.ToUniversalTime().Ticks == target.ProcessStartTicks);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; EndLocked(); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
}
