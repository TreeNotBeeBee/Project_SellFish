using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using VirtualKey = Windows.System.VirtualKey;

namespace WindowPilot;

internal sealed class NativeTextWindow : DetachedWindow
{
    private readonly NativeControlBinding _binding;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly MenuFlyoutItem _apply = new() { Text = "写回原控件" };
    private readonly MenuFlyoutItem _reload = new() { Text = "重新载入" };
    private readonly MenuFlyoutItem _pickSend = new() { Text = "选择发送按钮" };
    private readonly MenuFlyoutItem _send = new() { Text = "发送", KeyboardAcceleratorTextOverride = "Enter" };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherQueueTimer _timer;
    private bool _reading, _acting, _closed, _unavailable, _composing, _needsReview;
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
            const string hint = "Enter 发送，Shift+Enter 换行";
            _text.PlaceholderText = hint;
            AutomationProperties.SetHelpText(_text, hint);
            AddOptionsMenu();
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
            // 输入法可能先结束组词，再把用于选词的同一次回车交给 TextBox。松键后，下一次回车即可发送。
            _text.PreviewKeyUp += (_, e) =>
            {
                if (e.Key == VirtualKey.Enter || !KeyHeld(VirtualKey.Enter)) _compositionEnded = 0;
            };
            _text.PreviewKeyDown += async (_, e) =>
            {
                if (e.Key != VirtualKey.Enter || _composing || KeyHeld(VirtualKey.Shift) || KeyHeld(VirtualKey.Menu)) return;
                e.Handled = true;
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

    private void AddOptionsMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(_send);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(_apply);
        menu.Items.Add(_reload);
        menu.Items.Add(_pickSend);
        Identify(_send, "SendNativeText", "发送");
        Identify(_apply, "ApplyNativeText", "写回原控件");
        Identify(_reload, "ReloadNativeText", "重新载入原文");
        Identify(_pickSend, "PickSendButton", "选择原窗口的发送按钮");

        var options = new Button
        {
            Content = new FontIcon { Glyph = "\uE712", FontSize = 16 },
            Flyout = menu, VerticalAlignment = VerticalAlignment.Top,
            Width = 32, Height = 32, Padding = new Thickness(0)
        };
        Identify(options, "NativeTextOptions", "更多操作");
        ToolTipService.SetToolTip(options, "更多操作");
        Layout.ColumnDefinitions.Add(new ColumnDefinition());
        Layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Layout.ColumnSpacing = 4;
        Grid.SetColumn(options, 1);
        Layout.Children.Add(options);
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
                if (_submitted != null && Normalized(latest) != _submitted)
                {
                    // 发送后原应用清空输入框时，用户可能已经在浮窗起草下一条消息。
                    if (latest.Length == 0 && Dirty) _baseText = latest;
                    _submitted = null;
                }
                if (Normalized(latest) == Normalized(_text.Text)) _baseText = latest;
                else if (Conflict && !Dirty && !_composing) Load(latest);
            }
            SetStatus(_actionStatus ?? (Conflict ? "原控件已更改。可复制浮窗草稿，再点「⋯ → 重新载入」。" : ""));
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
        _submitted = Normalized(draft);
        _baseText = _latest = draft;
        _actionStatus = "已触发原窗口的发送按钮。";
    }
}
