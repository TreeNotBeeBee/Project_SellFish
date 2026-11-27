using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Windows.UI;

namespace WindowPilot;

public sealed record TargetChoice(string Display, WindowTarget? Target);

public sealed partial class MainWindow : Window
{
    private const int OpacityStep = 10;
    private readonly WindowService _windows = new(Path.Combine(AppProfile.DirectoryPath, "recovery.json"));
    private readonly EffectSession _effects = new();
    private readonly DispatcherQueueTimer _refresh;
    private readonly DispatcherQueueTimer _guard;
    private readonly PercentageInput _opacityInput;
    private readonly PercentageInput _tintInput;
    private readonly Color _defaultTint;
    private readonly CompactWindow _panel;
    private readonly NotificationAreaIcon? _tray;
    private ShortcutEditor? _shortcuts;
    private CancellationTokenSource? _windowPicking;
    private TargetChoice? _pendingChoice;
    private int _requestedOpacity = 100;
    private bool _invert;
    private bool _gray;
    private int _tintStrength;
    private bool _updating;
    private bool _ready;
    private long _retryGuardAt;

    private bool HasFilters => _invert || _gray || _tintStrength > 0 || EffectiveKeyStrength > 0;

    public MainWindow()
    {
        InitializeComponent();
        TargetBox.DropDownClosed += (_, _) =>
        {
            if (_pendingChoice != null) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CommitTargetChoice);
        };
        _defaultTint = TintColorPicker.Color;
        ShowTintColor(_defaultTint);
        _opacityInput = new(this, OpacityInput, "不透明度", () => _requestedOpacity, SetRequestedOpacity);
        _tintInput = new(this, TintStrengthInput, "滤镜强度", () => _tintStrength, SetTintStrength);
        _keyRangeInput = new(this, KeyRangeInput, "颜色范围", () => _keyRange, SetKeyRange);
        _keyStrengthInput = new(this, KeyStrengthInput, "过滤强度", () => _keyStrength, SetKeyStrength);
        KeyColorPicker.Color = DefaultKeyColor;
        ShowKeyColor(DefaultKeyColor);
        KeyColorPicker.ColorChanged += (_, args) =>
        {
            ShowKeyColor(args.NewColor);
            if (_ready && _windows.Target != null && EffectiveKeyStrength > 0) Run(ApplyAppearance);
        };
        TintColorPicker.ColorChanged += (_, args) =>
        {
            ShowTintColor(args.NewColor);
            if (_ready && _windows.Target != null && _tintStrength > 0) Run(ApplyAppearance);
        };
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "FishBasket.ico");
        AppWindow.SetIcon(iconPath);
        MiniToggle[] actions =
        [
            new("hide", HiddenBox, _ => Run(SwitchVisibility), "ImeHideIcon"),
            new("clickThrough", ClickThroughBox, _ => Run(SwitchThrough), "ImeThroughIcon"),
            new("topmost", TopmostBox, value => Run(() => SetTopmost(value)), "ImeTopmostIcon"),
            new("invert", InvertBox, value => Run(() => SetFilters(value, _gray)), "ImeInvertIcon"),
            new("gray", GrayBox, value => Run(() => SetFilters(_invert, value)), "ImeGrayIcon")
        ];
        var toolbarSettings = new MiniToolbarSettings(AppProfile.DirectoryPath, actions.Select(action => action.Id));
        _panel = new CompactWindow(this, Root, ThemeBox, StatusText, actions, toolbarSettings);
        PanelContent.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
        {
            if (PanelContent.Visibility == Visibility.Collapsed)
            {
                _windowPicking?.Cancel();
                _colorPicking?.Cancel();
            }
        });
        InitializeToolbarSettings(toolbarSettings, actions);
        InitializeToolPin();
        _refresh = DispatcherQueue.CreateTimer();
        _refresh.Interval = TimeSpan.FromSeconds(2);
        _refresh.Tick += (_, _) => Run(RefreshState);
        _guard = DispatcherQueue.CreateTimer();
        _guard.Interval = TimeSpan.FromMilliseconds(80);
        _guard.Tick += (_, _) => GuardTargetAppearance();
        _effects.Failed += HandleEffectFailure;
        try
        {
            _shortcuts = new ShortcutEditor(WindowNative.GetWindowHandle(this),
                (HideShortcutBox, HideShortcutClear, () => Run(SwitchVisibility)),
                (ThroughShortcutBox, ThroughShortcutClear, () => Run(SwitchThrough)),
                (OpacityUpShortcutBox, OpacityUpShortcutClear, () => Run(() => StepOpacity(OpacityStep))),
                (OpacityDownShortcutBox, OpacityDownShortcutClear, () => Run(() => StepOpacity(-OpacityStep))),
                Status);
        }
        catch (Exception ex) { Status(ex.Message); }
        try { _tray = new NotificationAreaIcon(this, iconPath, () => Run(_panel.RevealPanel)); }
        catch (Exception ex) { Status("通知区域图标不可用：" + ex.Message); }
        AppWindow.Closing += ClosingWindow;
        Closed += (_, _) =>
        {
            _ready = false;
            _windowPicking?.Cancel();
            _tray?.Dispose();
            _colorPicking?.Cancel();
            _refresh.Stop();
            _guard.Stop();
            _opacityInput.Stop();
            _tintInput.Stop();
            _keyRangeInput.Stop();
            _keyStrengthInput.Stop();
            _shortcuts?.Dispose();
            _effects.Dispose();
            _panel.Dispose();
            // Closing a WinUI window programmatically may bypass AppWindow.Closing.
            try { _windows.Release(); } catch { /* A pending recovery is retained on disk. */ }
        };
        _ready = true;
        if (_windows.Target is { } previous)
            ShowBoundChoice(previous);
        Run(RefreshState);
        SyncControls();
        if (_windows.RecoveryWarning is { } warning) Status(warning);
        _refresh.Start();
        _guard.Start();
    }

    private void Status(string message) => StatusText.Text = message;

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { Status(ex.Message); }
    }

    private void GuardTargetAppearance()
    {
        if (_windows.Target == null || Environment.TickCount64 < _retryGuardAt) return;
        try
        {
            _windows.EnforceAppearance();
            var interval = TimeSpan.FromMilliseconds(_windows.UsesBackdrop && !_windows.IsHidden ? 33 : 80);
            if (_guard.Interval != interval) _guard.Interval = interval;
        }
        catch (Exception ex)
        {
            _retryGuardAt = Environment.TickCount64 + 1500;
            Status(ex.Message);
        }
    }

    private void RefreshState()
    {
        if (_windows.Target == null)
        {
            RefreshList();
            return;
        }
        if (!_windows.IsAlive())
        {
            Release();
            Status("目标窗口已关闭，请重新选择。");
            return;
        }
        SyncControls();
    }

    private void RefreshList()
    {
        if (TargetBox.IsDropDownOpen || _windows.Target != null || _windowPicking != null || _pendingChoice != null) return;
        var choices = _windows.Enumerate()
            .OrderBy(w => w.Display, StringComparer.CurrentCultureIgnoreCase)
            .Select(w => new TargetChoice(w.Display, w)).ToList();
        if (TargetBox.ItemsSource is List<TargetChoice> old && old.SequenceEqual(choices)) return;
        _updating = true;
        try { TargetBox.ItemsSource = choices; }
        finally { _updating = false; }
    }

    private void ShowBoundChoice(WindowTarget target)
    {
        var bound = new TargetChoice(target.Display, target);
        _updating = true;
        try
        {
            TargetBox.ItemsSource = new[] { bound, new TargetChoice("释放并恢复窗口", null) };
            TargetBox.SelectedItem = bound;
        }
        finally { _updating = false; }
    }

    private void SelectTarget(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || _windowPicking != null || TargetBox.SelectedItem is not TargetChoice choice) return;
        // SelectionChanged 仍在下拉项的焦点/自动化回调中。此时更换 ItemsSource
        // 会销毁 WinUI 正在处理的列表项，先让弹层收起再完成绑定或释放。
        _pendingChoice = choice;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CommitTargetChoice);
    }

    private void CommitTargetChoice()
    {
        if (!_ready || _pendingChoice is not { } choice) return;
        if (TargetBox.IsDropDownOpen)
        {
            // UI Automation 的 Select 不一定会收起列表。DropDownClosed 会再次排队。
            TargetBox.IsDropDownOpen = false;
            return;
        }
        _pendingChoice = null;
        if (_windowPicking != null || TargetBox.SelectedItem is not TargetChoice selected || selected != choice) return;
        Run(() =>
        {
            if (choice.Target == null) Release();
            else BindTarget(choice.Target);
        });
    }

    private async void PickWindow(object sender, RoutedEventArgs args)
    {
        if (!_ready || _windowPicking != null || _pendingChoice != null || _windows.Target != null) return;
        using var cancellation = new CancellationTokenSource();
        _windowPicking = cancellation;
        const string hint = "请单击要绑定的窗口，按 Esc 取消。";
        try
        {
            TargetBox.IsDropDownOpen = false;
            SyncControls();
            Status(hint);
            var target = await ScreenPicker.PickWindowAsync(cancellation.Token);
            // 点击已捕获时仍可能切页或关闭面板，过期的结果不能建立新绑定。
            if (_ready && !cancellation.IsCancellationRequested) BindTarget(target);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_ready && !cancellation.IsCancellationRequested) Status(ex.Message); }
        finally
        {
            _windowPicking = null;
            if (_ready)
            {
                if (StatusText.Text == hint) Status("");
                Run(SyncControls);
            }
        }
    }

    private void BindTarget(WindowTarget target)
    {
        if (_windows.Target != null) return;
        try { _windows.Bind(target); }
        catch { TargetBox.SelectedIndex = -1; throw; }
        _requestedOpacity = _windows.Opacity ?? 100;
        ResetBindingFilters();
        _retryGuardAt = 0;
        ShowBoundChoice(target);
        _opacityInput.ShowValue();
        SyncControls();
        Status("");
    }

    private void SyncControls()
    {
        _updating = true;
        try
        {
            bool bound = _windows.Target != null;
            TargetBox.IsEnabled = _windowPicking == null;
            PickWindowButton.IsEnabled = !bound && _windowPicking == null;
            HiddenBox.IsEnabled = TopmostBox.IsEnabled = bound;
            HiddenBox.IsChecked = bound && _windows.IsHidden;
            TopmostBox.IsChecked = bound && _windows.IsTopmost;
            ClickThroughBox.IsEnabled = bound && _windows.CanClickThrough;
            ClickThroughBox.IsChecked = bound && _windows.IsClickThrough;
            bool supported = bound && _windows.Opacity.HasValue;
            OpacityInput.IsEnabled = InvertBox.IsEnabled = GrayBox.IsEnabled = supported;
            TintColorButton.IsEnabled = TintStrengthInput.IsEnabled = supported;
            InvertBox.IsChecked = _invert;
            GrayBox.IsChecked = _gray;
            RefreshColorKeyControls(supported);
        }
        finally { _updating = false; }
    }

    private void ShowTintColor(Color color)
    {
        TintSwatchBrush.Color = color;
        AutomationProperties.SetName(TintColorButton, $"滤镜颜色 #{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void Release()
    {
        // 恢复窗口前撤销延迟提交与屏幕取色，避免把旧输入应用到下一次绑定。
        _opacityInput.Stop();
        _tintInput.Stop();
        _keyRangeInput.Stop();
        _keyStrengthInput.Stop();
        _colorPicking?.Cancel();
        _pendingChoice = null;
        TintColorButton.Flyout.Hide();
        KeyColorButton.Flyout.Hide();
        _effects.Stop();
        _windows.Release();
        ResetBindingFilters();
        _requestedOpacity = 100;
        _opacityInput.ShowValue();
        SyncControls();
        TargetBox.IsDropDownOpen = false;
        // 目标也可能在下拉框打开时自行关闭，延后一轮重建未绑定的窗口列表。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (_ready) Run(RefreshList); });
        Status("");
    }

    private void ToggleHidden(object sender, RoutedEventArgs e) => Run(SwitchVisibility);
    private void SwitchVisibility()
    {
        if (_windows.Target == null) return;
        bool hide = !_windows.IsHidden;
        if (hide) _effects.Stop();
        try { _windows.SetHidden(hide); }
        finally
        {
            if (!_windows.IsHidden && HasFilters) ApplyAppearance();
            SyncControls();
        }
        Status("");
    }

    private void ToggleClickThrough(object sender, RoutedEventArgs e) => Run(SwitchThrough);
    private void SwitchThrough()
    {
        if (_windows.Target == null) return;
        try { _windows.SetClickThrough(!_windows.IsClickThrough); }
        finally { SyncControls(); }
        Status("");
    }

    private void ChangeTopmost(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating || _windows.Target == null) return;
        Run(() => SetTopmost(TopmostBox.IsChecked == true));
    }

    private void SetTopmost(bool enabled)
    {
        if (_windows.Target == null) return;
        try { _windows.SetTopmost(enabled); }
        finally { SyncControls(); }
        if (HasFilters) ApplyAppearance();
        Status("");
    }

    private void SetTintStrength(int value)
    {
        _tintStrength = value;
        ApplyAppearance();
        Status("");
    }

    private void StepOpacity(int delta)
    {
        if (!_ready || _windows.Target == null || !OpacityInput.IsEnabled) return;
        // 数字框延迟提交。若用户刚输入了有效值，从该值步进，只向目标窗口应用一次最终结果。
        int basis = _opacityInput.PendingOrCurrentValue;
        _opacityInput.Stop();
        int next = Math.Clamp(basis + delta, 0, 100);
        if (next != _requestedOpacity) SetRequestedOpacity(next);
        _opacityInput.ShowValue();
    }

    private void SetRequestedOpacity(int value)
    {
        if (_windows.Target == null) return;
        _requestedOpacity = value;
        ApplyAppearance();
        Status("");
    }

    private void ChangeFilters(object sender, RoutedEventArgs e) =>
        Run(() => SetFilters(InvertBox.IsChecked == true, GrayBox.IsChecked == true));

    private void SetFilters(bool invert, bool gray)
    {
        if (!_ready || _updating || _windows.Target == null || !InvertBox.IsEnabled) return;
        _invert = invert;
        _gray = gray;
        ApplyAppearance();
    }

    private void ResetFilters()
    {
        _invert = _gray = false;
        _tintStrength = 0;
        _tintInput.ShowValue();
        KeyEnabled.IsChecked = false;
    }

    private void ResetBindingFilters()
    {
        ResetFilters();
        TintColorPicker.Color = _defaultTint;
        KeyColorPicker.Color = DefaultKeyColor;
        _keyRange = 0;
        _keyStrength = 100;
        _keyRangeInput.ShowValue();
        _keyStrengthInput.ShowValue();
    }

    private void ApplyAppearance()
    {
        if (_windows.Target is not { } target) return;
        try
        {
            bool render = HasFilters && _requestedOpacity > 0 && !_windows.IsHidden;
            if (!render) _effects.Stop();
            // 滤镜由叠加层绘制；原窗口保留 1/255 的不透明度以接收鼠标输入。
            if (render) _windows.SetOpacityByte(1);
            else _windows.SetOpacity(_requestedOpacity);
            if (render) _effects.Update(target.Handle, _invert, _gray, _requestedOpacity, false, _windows.IsTopmost,
                TintColorPicker.Color, _tintStrength, KeyColorPicker.Color, _keyRange, EffectiveKeyStrength);
        }
        catch
        {
            _effects.Stop();
            ResetFilters();
            try { _windows.SetOpacity(_requestedOpacity); }
            finally { _requestedOpacity = _windows.Opacity ?? 100; _opacityInput.ShowValue(); }
            throw;
        }
        finally { SyncControls(); }
    }

    private void HandleEffectFailure(string message)
    {
        if (_windows.Target == null || !HasFilters) return;
        Run(() =>
        {
            ResetFilters();
            ApplyAppearance();
            Status("滤镜已关闭：" + message);
        });
    }

    private void ClosingWindow(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_windows.Target == null) return;
        try { Release(); }
        catch (Exception ex) { args.Cancel = true; Status("窗口未完全恢复：" + ex.Message); }
    }
}
