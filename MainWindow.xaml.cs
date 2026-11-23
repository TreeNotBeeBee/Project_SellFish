using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;
using WinRT.Interop;

namespace WindowPilot;

public sealed partial class MainWindow : Window
{
    private readonly WindowService _windows = new();
    private readonly EffectSession _effects = new();
    private readonly DispatcherQueueTimer _poll;
    private readonly DispatcherQueueTimer _opacityDelay;
    private GlobalShortcut? _hotkey;
    private bool _ready;
    private bool _updating;
    private int _requestedOpacity = 100;
    private int _previousOpacity = 100;
    private bool _invert;
    private bool _gray;

    public MainWindow()
    {
        InitializeComponent();
        nint hwnd = WindowNative.GetWindowHandle(this);
        double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        AppWindow.Resize(new SizeInt32(
            Math.Min((int)(550 * scale), display.WorkArea.Width),
            Math.Min((int)(700 * scale), display.WorkArea.Height)));
        _opacityDelay = DispatcherQueue.CreateTimer();
        _opacityDelay.Interval = TimeSpan.FromMilliseconds(55);
        _opacityDelay.IsRepeating = false;
        _opacityDelay.Tick += (_, _) => Run(() => SetRequestedOpacity((int)Math.Round(OpacitySlider.Value)));
        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromSeconds(2);
        _poll.Tick += (_, _) => Run(CheckWindow);
        _effects.Failed += HandleEffectFailure;
        AppWindow.Closing += ClosingWindow;
        Closed += (_, _) =>
        {
            _poll.Stop();
            _opacityDelay.Stop();
            _hotkey?.Dispose();
            _effects.Dispose();
        };
        var chosen = Shortcut.Load();
        ShortcutKey.ItemsSource = Shortcut.Keys;
        ShortcutKey.SelectedItem = chosen.KeyName;
        CtrlKey.IsChecked = (chosen.Modifiers & 2) != 0;
        AltKey.IsChecked = (chosen.Modifiers & 1) != 0;
        ShiftKey.IsChecked = (chosen.Modifiers & 4) != 0;
        WinKey.IsChecked = (chosen.Modifiers & 8) != 0;
        try
        {
            _hotkey = new GlobalShortcut(hwnd, () => DispatcherQueue.TryEnqueue(() => Run(SwitchVisibility)));
            _hotkey.Register(chosen);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        UpdateShortcutLabel();
        if (_windows.RecoveryWarning is { } warning)
            StatusText.Text = "上次的窗口恢复需要处理：" + warning;
        _ready = true;
        Run(RefreshList);
        _poll.Start();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private void RefreshList()
    {
        if (_windows.Target != null || TargetBox.IsDropDownOpen) return;
        var items = _windows.Enumerate().OrderBy(w => w.Display, StringComparer.CurrentCultureIgnoreCase).ToList();
        TargetBox.ItemsSource = items;
        BindingInfo.Text = $"检测到 {items.Count} 个窗口。选定后固定绑定，释放时复原。";
    }

    private void CheckWindow()
    {
        if (_windows.Target == null) { RefreshList(); return; }
        if (!_windows.IsAlive()) { Release(); StatusText.Text = "绑定的窗口已关闭，可以选择其他窗口。"; return; }
        SyncControls();
    }

    private void SelectTarget(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating || _windows.Target != null || TargetBox.SelectedItem is not WindowTarget target) return;
        Run(() =>
        {
            try { _windows.Bind(target); }
            catch { TargetBox.SelectedIndex = -1; throw; }
            _requestedOpacity = _windows.Opacity ?? 100;
            _previousOpacity = _requestedOpacity > 0 ? _requestedOpacity : 100;
            _invert = _gray = false;
            SyncControls();
            StatusText.Text = "已绑定 " + target.Title;
        });
    }

    private void SyncControls()
    {
        _updating = true;
        try
        {
            bool bound = _windows.Target != null;
            TargetBox.IsEnabled = !bound;
            BindAction.Content = bound ? "释放" : "刷新";
            TargetActions.IsEnabled = bound;
            if (!bound) return;
            TopmostBox.IsChecked = _windows.IsTopmost;
            VisibilityButton.Content = _windows.IsHidden ? "显示窗口" : "隐藏窗口";
            BindingInfo.Text = _windows.IsHidden ? "已隐藏 · 任务栏中的窗口按钮已收起" : "已绑定 · 释放将恢复绑定前的状态";
            bool supported = _windows.Opacity.HasValue;
            OpacitySlider.IsEnabled = ZeroButton.IsEnabled = InvertBox.IsEnabled = GrayBox.IsEnabled = supported;
            InvertBox.IsChecked = _invert;
            GrayBox.IsChecked = _gray;
            if (!_opacityDelay.IsRunning)
            {
                OpacitySlider.Value = _requestedOpacity;
                OpacityValue.Text = supported ? $"{_requestedOpacity}%" : "窗口不支持透明度";
            }
            ZeroButton.Content = _requestedOpacity == 0 ? $"回到 {_previousOpacity}%" : "一键全透明";
        }
        finally { _updating = false; }
    }

    private void RefreshOrRelease(object sender, RoutedEventArgs e) => Run(() =>
    {
        if (_windows.Target == null) RefreshList(); else Release();
    });

    private void Release()
    {
        _opacityDelay.Stop();
        _effects.Stop();
        _windows.Release();
        _invert = _gray = false;
        _requestedOpacity = _previousOpacity = 100;
        _updating = true;
        TargetBox.SelectedIndex = -1;
        InvertBox.IsChecked = GrayBox.IsChecked = TopmostBox.IsChecked = false;
        VisibilityButton.Content = "隐藏窗口";
        ZeroButton.Content = "一键全透明";
        OpacitySlider.Value = 100;
        OpacityValue.Text = "100%";
        _updating = false;
        SyncControls();
        RefreshList();
        StatusText.Text = "已释放窗口。";
    }

    private void ToggleHidden(object sender, RoutedEventArgs e) => Run(SwitchVisibility);
    private void SwitchVisibility()
    {
        if (_windows.Target == null) return;
        bool hide = !_windows.IsHidden;
        if (hide) _effects.Stop();
        try { _windows.SetHidden(hide); }
        finally { SyncControls(); }
        if (!hide && (_invert || _gray)) ApplyAppearance();
        StatusText.Text = hide ? "窗口已隐藏。再次按同一快捷键即可显示。" : "已显示绑定窗口。";
    }

    private void ChangeTopmost(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating) return;
        Run(() =>
        {
            try { _windows.SetTopmost(TopmostBox.IsChecked == true); }
            finally { SyncControls(); }
            if (_invert || _gray) ApplyAppearance();
            StatusText.Text = _windows.IsTopmost ? "已置顶。" : "已取消置顶。";
        });
    }

    private void ChangeOpacity(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_ready || _updating || _windows.Target == null) return;
        OpacityValue.Text = $"{Math.Round(e.NewValue)}%";
        _opacityDelay.Stop();
        _opacityDelay.Start();
    }
    private void ToggleZero(object sender, RoutedEventArgs e) => Run(() =>
    {
        _opacityDelay.Stop();
        SetRequestedOpacity(_requestedOpacity == 0 ? _previousOpacity : 0);
    });

    private void SetRequestedOpacity(int value)
    {
        if (_windows.Target == null) return;
        if (value == 0 && _requestedOpacity > 0) _previousOpacity = _requestedOpacity;
        _requestedOpacity = value;
        ApplyAppearance();
    }

    private void ChangeFilters(object sender, RoutedEventArgs e)
    {
        if (!_ready || _updating) return;
        _invert = InvertBox.IsChecked == true;
        _gray = GrayBox.IsChecked == true;
        Run(ApplyAppearance);
    }

    private void ApplyAppearance()
    {
        if (_windows.Target is not { } target) return;
        try
        {
            bool render = (_invert || _gray) && _requestedOpacity > 0 && !_windows.IsHidden;
            if (!render) _effects.Stop();
            // Keep the actual window hittable while the click-through filter displays its pixels.
            if (render) _windows.SetOpacityByte(1);
            else _windows.SetOpacity(_requestedOpacity);
            if (render) _effects.Update(target.Handle, _invert, _gray, _requestedOpacity, false, _windows.IsTopmost);
        }
        catch
        {
            _effects.Stop();
            _invert = _gray = false;
            try { _windows.SetOpacity(_requestedOpacity); }
            finally { _requestedOpacity = _windows.Opacity ?? 100; }
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
            StatusText.Text = "滤镜已关闭：" + message;
        });
    }

    private void SaveHotkey(object sender, RoutedEventArgs e) => Run(() =>
    {
        uint modifiers = (CtrlKey.IsChecked == true ? 2u : 0u) | (AltKey.IsChecked == true ? 1u : 0u) |
                         (ShiftKey.IsChecked == true ? 4u : 0u) | (WinKey.IsChecked == true ? 8u : 0u);
        if (_hotkey == null || ShortcutKey.SelectedItem is not string key)
            throw new InvalidOperationException("无法设置快捷键。请重新打开控制面板。");
        var chosen = new Shortcut(modifiers, Shortcut.FromName(key));
        _hotkey.Register(chosen);
        UpdateShortcutLabel();
        chosen.Save();
        StatusText.Text = "快捷键已保存。";
    });

    private void UpdateShortcutLabel() => ShortcutInfo.Text = _hotkey?.Active is { } key ?
        $"当前快捷键  {key}" : "当前无快捷键，请选择可用的组合。";

    private void ClosingWindow(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_windows.Target == null) return;
        try { Release(); }
        catch (Exception ex) { args.Cancel = true; StatusText.Text = "尚未释放窗口，请重试：" + ex.Message; }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
}
