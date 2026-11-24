using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowPilot;

public sealed partial class MainWindow
{
    private readonly Dictionary<DetachedWindow, (string Key, Grid Row)> _detached = [];
    private CancellationTokenSource? _picking;
    private BrowserBroker? _browser;
    private ChatAnswerWindow? _answers;
    private ChatInputWindow? _composer;
    private BrowserComposerLink? _composerLink;
    private bool _pinned;
    private static string PinPath => Path.Combine(AppProfile.DirectoryPath, "tool-topmost.txt");

    private void InitializeDetachedControls()
    {
        try { _pinned = File.Exists(PinPath) && File.ReadAllText(PinPath).Trim() == "1"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status("无法读取面板置顶设置。"); }
        ToolPin.IsChecked = _pinned;
        ApplyToolAppearance();
        ThemeBox.SelectionChanged += (_, _) => ApplyToolAppearance();
    }

    private void PageChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_ready) return;
        BasicPage.Visibility = sender.SelectedItem == BasicTab ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = sender.SelectedItem == AdvancedTab ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender.SelectedItem == SettingsTab ? Visibility.Visible : Visibility.Collapsed;
        if (sender.SelectedItem != AdvancedTab) _picking?.Cancel();
        RefreshAdvancedControls();
    }

    private void SetToolPin(object sender, RoutedEventArgs args) => Run(() =>
    {
        _pinned = ToolPin.IsChecked == true;
        ApplyToolAppearance();
        Directory.CreateDirectory(AppProfile.DirectoryPath);
        File.WriteAllText(PinPath + ".tmp", _pinned ? "1" : "0");
        File.Move(PinPath + ".tmp", PinPath, true);
        Status("");
    });

    private void ApplyToolAppearance()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop != _pinned)
            presenter.IsAlwaysOnTop = _pinned;
        foreach (var view in _detached.Keys) view.SetAppearance(_pinned, Root.RequestedTheme);
    }

    private static bool IsBrowser(WindowTarget? target) =>
        target?.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) == true ||
        target?.ProcessName.Equals("chrome", StringComparison.OrdinalIgnoreCase) == true;

    private void RefreshAdvancedControls()
    {
        var target = _windows.Target;
        PickControl.IsEnabled = target != null && _picking == null && !_windows.IsHidden;
        PairWeb.IsEnabled = IsBrowser(target) && _picking == null;
        PairWeb.Content = _browser?.State == null ? "连接网页" : "重新连接";
        OpenAnswers.IsEnabled = OpenComposer.IsEnabled = _browser is { Connected: true, State: not null };
        string hint = target == null ? "先到「基础」绑定要操作的窗口。" :
            _picking != null ? "单击目标窗口内的文本控件，按 Esc 取消。" :
            _browser != null ? _browser.Status :
            IsBrowser(target) ? "使用 ChatGPT 时，点「连接网页」，再点浏览器中的 sell fish 扩展。" :
            "单击「选择控件」可分离文本或可编辑的输入框。";
        if (AdvancedHint.Text != hint) AdvancedHint.Text = hint;
    }

    private async void PickNativeControl(object sender, RoutedEventArgs args)
    {
        if (_windows.Target is not { } target || _picking != null) return;
        using var cancellation = new CancellationTokenSource();
        _picking = cancellation;
        RefreshAdvancedControls();
        try
        {
            var binding = await NativeControlPicker.PickAsync(target, cancellation.Token);
            if (cancellation.IsCancellationRequested || _windows.Target != target || !_windows.IsAlive())
            { binding.Dispose(); return; }
            string key = "uia:" + binding.Key;
            var existing = _detached.FirstOrDefault(entry => entry.Value.Key == key).Key;
            if (existing != null)
            {
                binding.Dispose();
                existing.Activate();
                return;
            }
            NativeTextWindow view;
            try { view = new NativeTextWindow(binding); }
            catch { binding.Dispose(); throw; }
            ShowDetached(view, key, binding.Label);
            Status("");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) Status(ex.Message); }
        finally
        {
            if (_picking == cancellation) _picking = null;
            if (_ready) RefreshAdvancedControls();
        }
    }

    private void PairBrowser(object sender, RoutedEventArgs args) => Run(() =>
    {
        if (_windows.Target is not { } target || !IsBrowser(target)) return;
        // Preserve any unsent text in the old input window. A new pairing has its
        // own windows and command queue, even when it points at the same browser.
        LeaveWebConnection();
        var browser = new BrowserBroker(action => DispatcherQueue.TryEnqueue(() => action()));
        _browser = browser;
        browser.Changed += OnWebChanged;
        try { browser.BeginPairing(target); }
        catch { LeaveWebConnection(); throw; }
        RefreshAdvancedControls();
        Status("");
    });

    private void OpenAnswerView(object sender, RoutedEventArgs args) => Run(() =>
    {
        if (_browser is not { Connected: true, State: not null }) return;
        if (_answers != null) { _answers.Activate(); return; }
        var view = new ChatAnswerWindow();
        _answers = view;
        view.Closed += (_, _) => { if (_answers == view) _answers = null; };
        ShowDetached(view, "answers", "ChatGPT 回答");
        OnWebChanged();
    });

    private void OpenInputView(object sender, RoutedEventArgs args) => Run(() =>
    {
        if (_browser is not { Connected: true, State: not null } browser) return;
        if (_composer != null) { _composer.Activate(); return; }
        var view = new ChatInputWindow();
        var link = new BrowserComposerLink(browser, view);
        _composerLink = link;
        _composer = view;
        view.Closed += (_, _) =>
        {
            link.Dispose();
            if (_composer == view) { _composer = null; _composerLink = null; }
        };
        ShowDetached(view, "composer", "ChatGPT 输入");
        OnWebChanged();
    });

    private void OnWebChanged()
    {
        if (!_ready || _browser is not { } browser) return;
        RefreshAdvancedControls();
        _composer?.SetConnected(browser.Connected, browser.Status);
        if (browser.Connected && browser.State is { } state)
        {
            _answers?.SetStatus("");
            _answers?.SetAnswers(state.Blocks.Select(b => new AnswerBlock(b.Kind, b.Text)).ToArray());
            _composer?.SetRemoteDraft(state.Draft, state.CanSend);
        }
        else _answers?.SetStatus(browser.Status);
    }

    private void ShowDetached(DetachedWindow window, string key, string label)
    {
        string initialTitle = window.Title;
        int suffix = 1;
        while (_detached.Keys.Any(w => w.Title == window.Title))
            window.Title = $"{initialTitle} ({++suffix})";
        if (suffix > 1) label += $" ({suffix})";
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "关闭", Padding = new Thickness(7, 1, 7, 1), MinHeight = 24 };
        AutomationProperties.SetName(close, "关闭" + label);
        Grid.SetColumn(close, 1);
        close.Click += (_, _) => window.Close();
        row.Children.Add(name);
        row.Children.Add(close);
        _detached.Add(window, (key, row));
        DetachedRows.Children.Add(row);
        window.Closed += (_, _) => { _detached.Remove(window); DetachedRows.Children.Remove(row); };
        try { window.SetAppearance(_pinned, Root.RequestedTheme); window.Show(); }
        catch { window.Close(); throw; }
    }

    private void LeaveWebConnection()
    {
        _composerLink?.Dispose();
        _composerLink = null;
        const string closed = "连接已结束。可复制此窗口的内容，重新配对后打开新的浮窗。";
        _composer?.SetConnected(false, closed);
        _answers?.SetStatus(closed);
        MarkDisconnected(_composer);
        MarkDisconnected(_answers);
        _composer = null;
        _answers = null;
        if (_browser == null) return;
        _browser.Changed -= OnWebChanged;
        _browser.Dispose();
        _browser = null;
    }

    private void MarkDisconnected(DetachedWindow? view)
    {
        if (view == null || !_detached.TryGetValue(view, out var item)) return;
        view.Title += "（已断开）";
        if (item.Row.Children[0] is TextBlock label) label.Text += "（已断开）";
    }

    private void EndDetachedSession()
    {
        _picking?.Cancel();
        LeaveWebConnection();
        foreach (var view in _detached.Keys.ToArray()) view.Close();
    }
}
