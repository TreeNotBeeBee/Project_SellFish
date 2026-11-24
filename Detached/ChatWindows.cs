using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowPilot;

internal sealed record AnswerBlock(string Kind, string Text);

internal sealed class ChatAnswerWindow : DetachedWindow
{
    private readonly RichTextBlock _text = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly List<AnswerBlock> _rendered = [];
    private IReadOnlyList<AnswerBlock> _pending = [];

    internal ChatAnswerWindow() : base("回答内容 · sell fish", 470, 370, false)
    {
        Identify(_text, "ChatAnswers", "回答内容");
        _scroll.Content = _text;
        Layout.Children.Add(_scroll);
        _text.SelectionChanged += (_, _) =>
        {
            if (_text.SelectedText.Length == 0) DispatcherQueue.TryEnqueue(Render);
        };
    }

    internal void SetAnswers(IReadOnlyList<AnswerBlock> blocks)
    {
        _pending = blocks.ToArray();
        Render();
    }

    private void Render()
    {
        // Rebuilding a selected run while the user copies it loses their selection.
        if (_text.SelectedText.Length > 0 || _rendered.SequenceEqual(_pending)) return;
        bool follow = _scroll.ScrollableHeight - _scroll.VerticalOffset <= 3;
        int prefix = 0;
        while (prefix < _rendered.Count && prefix < _pending.Count && _rendered[prefix] == _pending[prefix]) prefix++;
        for (int index = _rendered.Count - 1; index >= prefix; index--)
        {
            _text.Blocks.RemoveAt(index);
            _rendered.RemoveAt(index);
        }
        for (int index = prefix; index < _pending.Count; index++)
        {
            var block = _pending[index];
            bool list = block.Kind is "list" or "li";
            var run = new Run { Text = block.Text };
            if (block.Kind == "code") run.FontFamily = new FontFamily("Consolas");
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, list ? 2 : 8) };
            paragraph.Inlines.Add(run);
            _text.Blocks.Add(paragraph);
            _rendered.Add(block);
        }
        if (follow)
        {
            _scroll.UpdateLayout();
            _scroll.ChangeView(null, _scroll.ScrollableHeight, null, true);
        }
    }
}

internal sealed class ChatInputWindow : DetachedWindow
{
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "输入消息" };
    private readonly Button _send = new() { Content = "发送" };
    private readonly Button _useRemote = new() { Content = "采用网页草稿" };
    private readonly Button _useLocal = new() { Content = "保留浮窗草稿" };
    private readonly DispatcherQueueTimer _typing;
    private readonly List<string> _announced = [];
    private string _remote = "", _basis = "";
    private string? _sending;
    private bool _connected, _canSend, _writing, _composing, _conflict, _uncertainSend, _dirty, _closed;
    private long _compositionEnded;
    internal event Action<string, string>? SendRequested;
    internal event Action<string, string>? DraftChanged;

    internal ChatInputWindow() : base("聊天输入框 · sell fish", 455, 240, true)
    {
        Identify(_text, "ChatDraft", "聊天输入框");
        Identify(_send, "SendChatDraft", "发送");
        Identify(_useRemote, "UseWebDraft", "采用网页草稿");
        Identify(_useLocal, "KeepFloatDraft", "保留浮窗草稿");
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        Layout.Children.Add(_text);
        var actions = AddActions();
        actions.Children.Add(_useRemote);
        actions.Children.Add(_useLocal);
        actions.Children.Add(_send);
        _typing = DispatcherQueue.CreateTimer();
        _typing.IsRepeating = false;
        _typing.Interval = TimeSpan.FromMilliseconds(350);
        _typing.Tick += (_, _) => AnnounceDraft();
        _text.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) =>
        {
            if (_writing) return;
            _dirty = Local != _basis;
            QueueDraft();
            UpdateActions();
        });
        _text.TextCompositionStarted += (_, _) => { _composing = true; _typing.Stop(); };
        _text.TextCompositionEnded += (_, _) =>
        {
            _composing = false;
            _compositionEnded = Environment.TickCount64;
            QueueDraft();
            UpdateActions();
        };
        _text.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter || _composing || ShiftHeld()) return;
            e.Handled = true;
            // Some IMEs commit composition immediately before WinUI routes the same Enter.
            if (!e.KeyStatus.WasKeyDown && Environment.TickCount64 - _compositionEnded > 180) Submit();
        };
        _send.Click += (_, _) => Submit();
        _useRemote.Click += (_, _) => { _conflict = _uncertainSend = false; _announced.Clear(); Load(_remote); SetStatus(""); };
        _useLocal.Click += (_, _) =>
        {
            _conflict = _uncertainSend = false;
            _basis = _remote;
            _dirty = Local != _basis;
            _announced.Clear();
            SetStatus("");
            AnnounceDraft();
            UpdateActions();
        };
        Closed += (_, _) => { _closed = true; _typing.Stop(); };
        UpdateActions();
    }

    private static bool ShiftHeld() =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) &
         Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    private string Local => _text.Text.ReplaceLineEndings("\n");
    private void Load(string text)
    {
        _writing = true;
        try { if (Local != text) _text.Text = text; }
        finally { _writing = false; }
        _basis = text;
        _dirty = false;
        UpdateActions();
    }

    internal void SetRemoteDraft(string text, bool canSend)
    {
        _remote = text.ReplaceLineEndings("\n");
        _canSend = canSend;
        if (_sending != null || _uncertainSend) { UpdateActions(); return; }
        if (_remote == Local)
        {
            _basis = _remote;
            _dirty = _conflict = false;
            _announced.Clear();
        }
        else if (!_dirty && !_composing && !_conflict) Load(_remote);
        else if (_remote != _basis)
        {
            int echo = _announced.IndexOf(_remote);
            if (echo >= 0)
            {
                _announced.RemoveRange(0, echo + 1);
                _basis = _remote;
                QueueDraft();
            }
            else Conflict("网页和浮窗的草稿都已更改。请选择要保留的文字。");
        }
        UpdateActions();
    }

    internal void SetConnected(bool connected, string message)
    {
        _connected = connected;
        if (!connected)
        {
            _typing.Stop();
            if (_sending != null) _uncertainSend = true;
            _sending = null;
            _announced.Clear();
            if (_dirty || _uncertainSend) _conflict = true;
        }
        if (!_conflict || !connected) SetStatus(message);
        else SetStatus("浮窗保留了草稿。请选择要继续使用的文字。");
        UpdateActions();
    }

    internal void CompleteDraft(bool ok, string message)
    {
        if (!ok)
        {
            _announced.Clear();
            Conflict(string.IsNullOrEmpty(message) ? "网页尚未接受草稿。请选择要保留的文字。" : message);
        }
    }

    internal void CompleteSend(bool confirmed, string message)
    {
        if (_sending == null) return;
        string submitted = _sending;
        _sending = null;
        if (confirmed)
        {
            if (_remote == submitted) _remote = "";
            _conflict = false;
            Load(_remote);
            SetStatus(message);
        }
        else
        {
            _dirty = true;
            _uncertainSend = true;
            Conflict(string.IsNullOrEmpty(message) ? "发送未确认。请先在网页核对，草稿已保留。" : message);
        }
        UpdateActions();
    }

    private void Conflict(string message)
    {
        _typing.Stop();
        _conflict = true;
        SetStatus(message);
        UpdateActions();
    }

    private void QueueDraft()
    {
        _typing.Stop();
        if (!_closed && !_composing && _connected && _dirty && !_conflict && _sending == null) _typing.Start();
    }

    private void AnnounceDraft()
    {
        if (_closed || !_connected || !_dirty || _conflict || _composing || _sending != null) return;
        string value = Local;
        if (_announced.LastOrDefault() == value) return;
        _announced.Add(value);
        if (_announced.Count > 16) _announced.RemoveAt(0);
        DraftChanged?.Invoke(value, _basis);
    }

    private void Submit()
    {
        if (!_send.IsEnabled || _composing || SendRequested == null) return;
        _typing.Stop();
        _sending = Local;
        UpdateActions();
        SendRequested(_sending, _remote);
    }

    private void UpdateActions()
    {
        _text.IsReadOnly = _sending != null;
        _useLocal.Visibility = _useRemote.Visibility = _connected && _conflict ? Visibility.Visible : Visibility.Collapsed;
        _send.IsEnabled = _connected && !_conflict && !_composing && _sending == null &&
            !string.IsNullOrWhiteSpace(Local) && (_canSend || _dirty);
    }
}
