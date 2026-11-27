using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VirtualKey = Windows.System.VirtualKey;
using WinRT.Interop;

namespace WindowPilot;

public sealed record TargetChoice(string Display, WindowTarget? Target);

public sealed partial class MainWindow : Window
{
    private const int OpacityStep = 10;
    private readonly WindowService _windows = new(Path.Combine(AppProfile.DirectoryPath, "recovery.json"));
    private readonly EffectSession _effects = new();
    private readonly DispatcherQueueTimer _refresh;
    private readonly DispatcherQueueTimer _guard;
    private readonly DispatcherQueueTimer _typing;
    private readonly CompactWindow _panel;
    private readonly NotificationAreaIcon? _tray;
    private ShortcutEditor? _shortcuts;
    private int _requestedOpacity = 100;
    private int _previousOpacity = 100;
    private bool _invert;
    private bool _gray;
    private bool _updating;
    private bool _ready;
    private bool _writeOpacityText;
    private long _retryGuardAt;

    public MainWindow()
    {
        InitializeComponent();
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
        InitializeToolbarSettings(toolbarSettings, actions);
        InitializeDetachedControls();
        _typing = DispatcherQueue.CreateTimer();
        _typing.Interval = TimeSpan.FromMilliseconds(320);
        _typing.IsRepeating = false;
        _typing.Tick += (_, _) => Run(() => CommitNumber(finalize: false));
        OpacityInput.TextChanged += (_, _) =>
        {
            if (!_ready || _updating || _writeOpacityText || _windows.Target == null) return;
            _typing.Stop();
            _typing.Start();
        };

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
            _tray?.Dispose();
            EndDetachedSession();
            _refresh.Stop();
            _guard.Stop();
            _typing.Stop();
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
        if (TargetBox.IsDropDownOpen || _windows.Target != null) return;
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
        if (!_ready || _updating || TargetBox.SelectedItem is not TargetChoice choice) return;
        Run(() =>
        {
            if (choice.Target == null) { Release(); return; }
            if (_windows.Target != null) return;
            try { _windows.Bind(choice.Target); }
            catch { TargetBox.SelectedIndex = -1; throw; }
            _requestedOpacity = _windows.Opacity ?? 100;
            _previousOpacity = _requestedOpacity > 0 ? _requestedOpacity : 100;
            _invert = _gray = false;
            _retryGuardAt = 0;
            ShowBoundChoice(choice.Target);
            ShowOpacityNumber();
            SyncControls();
            Status("");
        });
    }

    private void SyncControls()
    {
        _updating = true;
        try
        {
            bool bound = _windows.Target != null;
            HiddenBox.IsEnabled = TopmostBox.IsEnabled = bound;
            HiddenBox.IsChecked = bound && _windows.IsHidden;
            TopmostBox.IsChecked = bound && _windows.IsTopmost;
            ClickThroughBox.IsEnabled = bound && _windows.CanClickThrough;
            ClickThroughBox.IsChecked = bound && _windows.IsClickThrough;
            bool supported = bound && _windows.Opacity.HasValue;
            OpacityInput.IsEnabled = ZeroButton.IsEnabled = InvertBox.IsEnabled = GrayBox.IsEnabled = supported;
            InvertBox.IsChecked = _invert;
            GrayBox.IsChecked = _gray;
            ZeroButton.Content = _requestedOpacity == 0 ? $"{_previousOpacity}%" : "0%";
            RefreshAdvancedControls();
        }
        finally { _updating = false; }
    }

    private void ShowOpacityNumber()
    {
        _writeOpacityText = true;
        try { OpacityInput.Text = _requestedOpacity.ToString(CultureInfo.InvariantCulture); }
        finally { _writeOpacityText = false; }
    }

    private void Release()
    {
        // 先撤销待提交的透明度和分离控件，再恢复窗口，避免迟到的回调操作旧目标。
        _typing.Stop();
        EndDetachedSession();
        _effects.Stop();
        _windows.Release();
        _invert = _gray = false;
        _requestedOpacity = _previousOpacity = 100;
        ShowOpacityNumber();
        SyncControls();
        // SelectionChanged may be running inside the open binding list.
        TargetBox.IsDropDownOpen = false;
        RefreshList();
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
            if (!_windows.IsHidden && (_invert || _gray)) ApplyAppearance();
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
        if (_invert || _gray) ApplyAppearance();
        Status("");
    }

    private void CommitOpacity(object sender, RoutedEventArgs e)
    {
        if (_ready) Run(() => CommitNumber(finalize: true));
    }

    private void OpacityInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Run(() => CommitNumber(finalize: true));
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            _typing.Stop();
            ShowOpacityNumber();
        }
    }

    private void CommitNumber(bool finalize)
    {
        _typing.Stop();
        if (_windows.Target == null || !OpacityInput.IsEnabled) return;
        if (!TryReadOpacity(out int value))
        {
            if (finalize)
            {
                ShowOpacityNumber();
                Status("不透明度可输入 0 到 100 的整数。");
            }
            return;
        }
        if (value != _requestedOpacity) SetRequestedOpacity(value);
        if (finalize) ShowOpacityNumber();
    }

    private bool TryReadOpacity(out int value)
    {
        string raw = OpacityInput.Text.Trim().TrimEnd('%', '％').Trim();
        return int.TryParse(raw, NumberStyles.None, CultureInfo.CurrentCulture, out value) && value is >= 0 and <= 100;
    }

    private void StepOpacity(int delta)
    {
        if (!_ready || _windows.Target == null || !OpacityInput.IsEnabled) return;
        // 数字框延迟提交。若用户刚输入了有效值，从该值步进，只向目标窗口应用一次最终结果。
        int basis = _typing.IsRunning && TryReadOpacity(out int pending) ? pending : _requestedOpacity;
        _typing.Stop();
        int next = Math.Clamp(basis + delta, 0, 100);
        if (next != _requestedOpacity) SetRequestedOpacity(next);
        ShowOpacityNumber();
    }

    private void ToggleZero(object sender, RoutedEventArgs e) => Run(() =>
    {
        _typing.Stop();
        SetRequestedOpacity(_requestedOpacity == 0 ? _previousOpacity : 0);
        ShowOpacityNumber();
    });

    private void SetRequestedOpacity(int value)
    {
        if (_windows.Target == null) return;
        if (value == 0 && _requestedOpacity > 0) _previousOpacity = _requestedOpacity;
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

    private void ApplyAppearance()
    {
        if (_windows.Target is not { } target) return;
        try
        {
            bool render = (_invert || _gray) && _requestedOpacity > 0 && !_windows.IsHidden;
            if (!render) _effects.Stop();
            // 滤镜由叠加层绘制；原窗口保留 1/255 的不透明度以接收鼠标输入。
            if (render) _windows.SetOpacityByte(1);
            else _windows.SetOpacity(_requestedOpacity);
            if (render) _effects.Update(target.Handle, _invert, _gray, _requestedOpacity, false, _windows.IsTopmost);
        }
        catch
        {
            _effects.Stop();
            _invert = _gray = false;
            try { _windows.SetOpacity(_requestedOpacity); }
            finally { _requestedOpacity = _windows.Opacity ?? 100; ShowOpacityNumber(); }
            throw;
        }
        finally { SyncControls(); }
    }

    private void HandleEffectFailure(string message)
    {
        if (_windows.Target == null || !(_invert || _gray)) return;
        Run(() =>
        {
            _invert = _gray = false;
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
