using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowPilot;

internal sealed class NativeTextWindow : DetachedWindow
{
    private readonly NativeControlBinding _binding;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Content = "写回原控件" };
    private readonly Button _reload = new() { Content = "重新载入" };
    private readonly Button _pickSend = new() { Content = "选择发送按钮" };
    private readonly Button _send = new() { Content = "发送" };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherQueueTimer _timer;
    private bool _reading, _acting, _closed, _unavailable, _composing, _needsReview, _sentBefore;
    private int _revision;
    private long _compositionEnded;
    private string? _submitted;
    private string? _actionStatus;
    private string _baseText;
    private string _latest;
    private static string Normalized(string text) => text.ReplaceLineEndings("\n");

    internal NativeTextWindow(NativeControlBinding binding) : base(binding.Label + " · sell fish", binding.Writable ? 490 : 420, 250, binding.Writable)
    {
        _binding = binding;
        _text.IsReadOnly = !binding.Writable;
        _baseText = _latest = binding.InitialText;
        _text.Text = _baseText;
        Identify(_text, "NativeText", binding.Label);
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        Layout.Children.Add(_text);
        if (binding.Writable)
        {
            var actions = AddActions();
            actions.Children.Add(_reload);
            actions.Children.Add(_apply);
            actions.Children.Add(_pickSend);
            actions.Children.Add(_send);
            Identify(_apply, "ApplyNativeText", "写回原控件");
            Identify(_reload, "ReloadNativeText", "重新载入原文");
            Identify(_pickSend, "PickSendButton", "选择原窗口的发送按钮");
            Identify(_send, "SendNativeText", "发送");
            ToolTipService.SetToolTip(_send, "Ctrl+Enter 发送。选定发送按钮或成功发送过后，Enter 发送，Shift+Enter 换行。");
            _apply.Click += async (_, _) => await RunActionAsync(ApplyAsync);
            _pickSend.Click += async (_, _) => await RunActionAsync(PickSendButtonAsync);
            _send.Click += async (_, _) => await RunActionAsync(SendAsync);
            _reload.Click += async (_, _) => await RunActionAsync(async () =>
            {
                string actual = await _binding.ReadAsync();
                if (_closed) return;
                _needsReview = false;
                _submitted = null;
                Load(actual);
            });
            _text.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) =>
            {
                if (!_needsReview && !_acting) _actionStatus = null;
                UpdateActions();
            });
            _text.TextCompositionStarted += (_, _) => { _composing = true; UpdateActions(); };
            _text.TextCompositionEnded += (_, _) => { _composing = false; _compositionEnded = Environment.TickCount64; UpdateActions(); };
            _text.PreviewKeyDown += async (_, e) =>
            {
                if (e.Key != VirtualKey.Enter || _composing || KeyHeld(VirtualKey.Shift)) return;
                // 普通编辑器仍用回车换行，确认过发送目标的浮窗才采用聊天框的回车行为。
                if (!KeyHeld(VirtualKey.Control) && !_binding.HasSendButton && !_sentBefore) return;
                e.Handled = true;
                // 输入法有时先结束组词，再把同一次回车交给 TextBox。
                if (_send.IsEnabled && !e.KeyStatus.WasKeyDown && Environment.TickCount64 - _compositionEnded > 180)
                    await RunActionAsync(SendAsync);
            };
            UpdateActions();
        }
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(800);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _lifetime.Cancel();
            _binding.Dispose();
            _lifetime.Dispose();
        };
    }

    private bool Dirty => Normalized(_text.Text) != Normalized(_baseText);
    private bool Conflict => Normalized(_latest) != Normalized(_baseText);
    private static bool KeyHeld(VirtualKey key) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) &
            Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
    private void UpdateActions()
    {
        bool edit = !_acting && !_unavailable && !_composing;
        _apply.IsEnabled = edit && Dirty && !Conflict && !_needsReview;
        _reload.IsEnabled = edit && (Dirty || Conflict || _needsReview || _submitted != null);
        _pickSend.IsEnabled = edit && !_needsReview;
        _send.IsEnabled = edit && !_needsReview && !Conflict && !string.IsNullOrWhiteSpace(_text.Text) &&
            Normalized(_text.Text) != _submitted;
    }
    private void Load(string value)
    {
        _baseText = _latest = value;
        _text.Text = value;
        UpdateActions();
    }

    private async Task RefreshAsync()
    {
        if (_closed || _reading || _acting) return;
        _reading = true;
        int revision = _revision;
        try
        {
            string latest = await _binding.ReadAsync();
            // 一次读取可能比用户发起的写回更晚返回，旧快照不能盖掉新的草稿。
            if (_closed || revision != _revision) return;
            _unavailable = false;
            _latest = latest;
            if (!_needsReview)
            {
                if (_submitted != null && Normalized(latest) != _submitted) _submitted = null;
                if (Normalized(latest) == Normalized(_text.Text)) _baseText = latest;
                else if (Conflict && !Dirty && !_composing) Load(latest);
            }
            SetStatus(_actionStatus ?? (Conflict ? "原控件已更改。浮窗保留了你的文字，可复制后重新载入。" : ""));
        }
        catch (Exception ex)
        {
            if (!_closed && revision == _revision)
            {
                _unavailable = true;
                SetStatus(ex.Message);
            }
        }
        finally { _reading = false; if (!_closed) UpdateActions(); }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_closed || _acting) return;
        _revision++;
        _acting = true;
        _actionStatus = null;
        SetStatus("");
        _text.IsReadOnly = true;
        UpdateActions();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) _actionStatus = ex.Message; }
        finally
        {
            _acting = false;
            if (!_closed)
            {
                _text.IsReadOnly = !_binding.Writable;
                SetStatus(_actionStatus ?? "");
                UpdateActions();
                await RefreshAsync();
            }
        }
    }

    private string Replacement => _text.Text.ReplaceLineEndings(_baseText.Contains("\r\n") ? "\r\n" : "\n");

    private async Task ApplyAsync()
    {
        string accepted = await _binding.WriteAsync(_baseText, Replacement);
        if (!_closed) Load(accepted);
    }

    private async Task PickSendButtonAsync()
    {
        SetStatus("单击原窗口里的发送按钮，按 Esc 取消。");
        await _binding.AttachSendButtonAsync(_lifetime.Token);
        if (!_closed) _actionStatus = "已选定原窗口的发送按钮。";
    }

    private async Task SendAsync()
    {
        string draft = Replacement;
        try { await _binding.SendAsync(_baseText, draft); }
        catch (SendOutcomeUnknownException)
        {
            // Invoke 已经跨进程，并不保证能收到回执。保留草稿，须核对原页再重新载入。
            _needsReview = true;
            throw;
        }
        if (_closed) return;
        _sentBefore = true;
        _submitted = Normalized(draft);
        _baseText = _latest = draft;
        _actionStatus = "已触发原窗口的发送按钮。";
    }
}
