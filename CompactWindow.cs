using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace WindowPilot;

/// <summary>Two fixed-size presentations of the same controller and window binding.</summary>
internal sealed class CompactWindow : IDisposable
{
    private const int ContentWidth = 536;
    private const int ContentHeight = 264;
    private const int FeedbackHeight = 40;
    private const nuint WorkAreaSubclass = 0x5741;

    private readonly Window _window;
    private readonly Grid _root;
    private readonly ComboBox _theme;
    private readonly TextBlock _status;
    private readonly Grid _fullPanel;
    private readonly StackPanel _miniActions;
    private readonly StackPanel _caption;
    private readonly Button _collapse;
    private readonly Button _expand;
    private readonly Button _exit;
    private readonly MiniToolbar _toolbar;
    private readonly MiniToolbarSettings _toolbarSettings;
    private readonly ImeToolbar _imeBar;
    private readonly MenuFlyoutItem _switchMode = new();
    private readonly nint _hwnd;
    private readonly MenuFlyout _menu = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _dragTimer;
    private readonly SubclassProc _workAreaProc;
    private readonly long _statusToken;
    private bool _disposed;
    private bool _sizeQueued;
    private bool _feedbackVisible;
    private bool _mini;
    private XamlRoot? _xamlRoot;
    private uint _sizeDpi;
    private (SizeInt32 Request, SizeInt32 Actual)? _lastResize;
    private uint? _dragPointer;
    private PointInt32 _dragOffset;
    private uint _dragDpi;

    public CompactWindow(Window window, Grid root, ComboBox theme, TextBlock status,
        MiniToggle[] actions, MiniToolbarSettings settings)
    {
        _window = window;
        _root = root;
        _theme = theme;
        _status = status;
        _hwnd = WindowNative.GetWindowHandle(window);
        _fullPanel = (Grid)root.FindName("PanelContent");
        _miniActions = (StackPanel)root.FindName("MiniActions");
        _caption = (StackPanel)root.FindName("CaptionButtons");
        _collapse = (Button)root.FindName("CollapseButton");
        _expand = (Button)root.FindName("ExpandButton");
        _exit = (Button)root.FindName("ExitButton");
        _toolbar = new MiniToolbar(actions);
        _toolbarSettings = settings;
        _imeBar = new ImeToolbar { Visibility = Visibility.Collapsed };
        root.Children.Add(_imeBar);
        _toolbarSettings.Changed += OnToolbarSettingsChanged;
        // A moving window can stay under the same local pointer coordinate. Sampling
        // screen coordinates while held also covers gaps in XAML PointerMoved events.
        _dispatcher = window.DispatcherQueue;
        _dragTimer = _dispatcher.CreateTimer();
        _dragTimer.Interval = TimeSpan.FromMilliseconds(16);
        _dragTimer.Tick += OnDragTick;

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        ExcludeControllerFromSwitchers();
        SizeWindow();
        if (WorkAreaBounds.ForWindow(_hwnd) is { } area) CenterIn(area);
        window.AppWindow.Changed += OnAppWindowChanged;
        _workAreaProc = OnWorkAreaMessage;
        // 即使系统拒绝安装消息监听，拖动和展开时仍会直接读取最新工作区。
        SetWindowSubclass(_hwnd, _workAreaProc, WorkAreaSubclass, 0);

        _collapse.Click += ToggleMode;
        _expand.Click += ToggleMode;
        _imeBar.Expand.Click += ToggleMode;
        _exit.Click += RequestClose;
        _switchMode.Text = "收起为工具栏";
        _switchMode.Click += ToggleMode;
        _menu.Items.Add(_switchMode);
        var exit = new MenuFlyoutItem { Text = "退出", KeyboardAcceleratorTextOverride = "Alt+F4" };
        exit.Click += RequestClose;
        _menu.Items.Add(exit);

        _theme.SelectedIndex = LoadTheme() == ElementTheme.Dark ? 1 : 0;
        ApplyTheme();
        _theme.SelectionChanged += OnThemeSelected;
        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.PointerReleased += OnPointerReleased;
        _root.PointerCanceled += OnPointerEnded;
        _root.PointerCaptureLost += OnPointerEnded;
        _window.Activated += OnWindowActivated;
        _root.RightTapped += OnRightTapped;
        _root.Loaded += OnLoaded;
        _statusToken = status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateFeedback());
        UpdateFeedback();
    }

    private int Pixels(double dips) => (int)Math.Ceiling(dips * _sizeDpi / 96.0);

    private void CenterIn(RectInt32 area)
    {
        var size = _window.AppWindow.Size;
        MoveWithin(new PointInt32(area.X + (area.Width - size.Width) / 2,
            area.Y + (area.Height - size.Height) / 2), size, area);
    }

    public void RevealPanel()
    {
        EndDrag();
        if (_window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        SetMiniMode(false);
        var area = GetCursorPos(out var cursor) ? WorkAreaBounds.ForPoint(cursor) : WorkAreaBounds.ForWindow(_hwnd);
        if (area.HasValue)
        {
            CenterIn(area.Value);
            // 跨缩放比例找回时，先在目标显示器重算面板尺寸，再以新尺寸居中。
            SizeWindow();
            CenterIn(area.Value);
        }
        _window.AppWindow.Show();
        _window.Activate();
        SetForegroundWindow(_hwnd);
    }

    private nint OnWorkAreaMessage(nint hwnd, uint message, nuint wp, nint lp, nuint id, nuint data)
    {
        // 任务栏移动/改高会广播 WM_SETTINGCHANGE，断开显示器或改变分辨率会广播 WM_DISPLAYCHANGE。
        if (message is 0x001A or 0x007E) QueueSizeWindow();
        return DefSubclassProc(hwnd, message, wp, lp);
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange && !_dragPointer.HasValue) QueueSizeWindow();
    }

    private void QueueSizeWindow()
    {
        if (_disposed || _sizeQueued) return;
        _sizeQueued = _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _sizeQueued = false;
            SizeWindow();
        });
    }

    private void ExcludeControllerFromSwitchers()
    {
        try { _window.AppWindow.IsShownInSwitchers = false; }
        catch (NotImplementedException)
        {
            // Windows 10 returns E_NOTIMPL. Set the tool-window style before the first Activate.
            const int extendedStyle = -20, toolWindow = 0x80, appWindow = 0x40000;
            int style = GetWindowLongW(_hwnd, extendedStyle);
            if (style == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            int previous = SetWindowLongW(_hwnd, extendedStyle, (style | toolWindow) & ~appWindow);
            if (previous == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            const uint frameChangedOnly = 0x20 | 0x10 | 0x4 | 0x2 | 0x1;
            if (!SetWindowPos(_hwnd, 0, 0, 0, 0, 0, frameChangedOnly))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private void ToggleMode(object sender, RoutedEventArgs args) => SetMiniMode(!_mini);

    private void OnToolbarSettingsChanged()
    {
        if (_mini) UpdatePresentation();
    }

    private void SetMiniMode(bool mini)
    {
        if (_mini == mini) return;
        _mini = mini;
        UpdatePresentation();
        var focus = mini ? (_toolbarSettings.UsesIcons ? _imeBar.Expand : _expand) : _collapse;
        focus.Focus(FocusState.Programmatic);
    }

    private void UpdatePresentation()
    {
        _lastResize = null;
        bool icons = _mini && _toolbarSettings.UsesIcons;
        bool classic = _mini && !icons;
        bool inputMethod = icons && _toolbarSettings.Style == MiniToolbarStyle.InputMethod;
        // Windows 会给带原生边框的窗口保留最小高度。输入法条使用无边框窗口，才能贴合 22 DIP 高的图标。
        var presenter = _window.AppWindow.Presenter as OverlappedPresenter;
        bool needsBorder = !inputMethod;
        bool changingBorder = presenter != null && presenter.HasBorder != needsBorder;
        if (changingBorder) presenter!.SetBorderAndTitleBar(needsBorder, false);
        _fullPanel.Visibility = _collapse.Visibility = _mini ? Visibility.Collapsed : Visibility.Visible;
        _miniActions.Visibility = _expand.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        _caption.Visibility = icons ? Visibility.Collapsed : Visibility.Visible;
        _imeBar.Visibility = icons ? Visibility.Visible : Visibility.Collapsed;
        _caption.VerticalAlignment = classic ? VerticalAlignment.Center : VerticalAlignment.Top;
        _caption.Margin = new Thickness(0, classic ? 0 : 2, 3, 0);
        if (_mini)
        {
            if (icons) _imeBar.SetInputMethodSkin(inputMethod);
            var host = icons ? _imeBar.Actions : _miniActions;
            var style = icons ? _imeBar.Resources[inputMethod ? "KeyboardToggle" : "ImeToggle"] : _root.Resources["ToolbarToggle"];
            _toolbar.Show(host, _toolbarSettings.GetActionIds(), (Style)style, icons ? _imeBar.Resources : null, inputMethod);
        }
        else _toolbar.Clear();
        _switchMode.Text = _mini ? "展开主面板" : "收起为工具栏";
        SizeWindow();
        // Windows 10 在后续消息中更新边框尺寸，用最终的非客户区尺寸再核对一次客户区。
        if (changingBorder) QueueSizeWindow();
    }

    // Follow the system close path so AppWindow.Closing can cancel a failed target restore.
    private void RequestClose(object sender, RoutedEventArgs args) => PostMessageW(_hwnd, 0x0010 /* WM_CLOSE */, 0, 0);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _root.Loaded -= OnLoaded;
        _xamlRoot = _root.XamlRoot;
        _xamlRoot.Changed += OnXamlRootChanged;
        SizeWindow();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Max(96u, GetDpiForWindow(_hwnd)) != _sizeDpi) SizeWindow();
    }

    private void SizeWindow()
    {
        if (_disposed || _window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
            || WorkAreaBounds.ForWindow(_hwnd) is not { } area) return;
        uint dpi = Math.Max(96u, GetDpiForWindow(_hwnd));
        if (dpi != _sizeDpi) _lastResize = null;
        _sizeDpi = dpi;
        var appWindow = _window.AppWindow;
        double width = ContentWidth;
        double height = ContentHeight + (_feedbackVisible ? FeedbackHeight : 0);
        if (_mini)
        {
            var unlimited = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
            if (_toolbarSettings.UsesIcons)
            {
                _imeBar.Measure(unlimited);
                width = _imeBar.DesiredSize.Width;
                height = _imeBar.DesiredSize.Height;
            }
            else
            {
                _miniActions.Measure(unlimited);
                _caption.Measure(unlimited);
                width = _miniActions.DesiredSize.Width + _caption.DesiredSize.Width;
                height = Math.Max(36, Math.Max(_miniActions.DesiredSize.Height, _caption.DesiredSize.Height));
            }
        }
        // 用实际非客户区尺寸算出整个窗口的大小，边框也需要留在工作区之内。
        var size = appWindow.Size;
        var client = appWindow.ClientSize;
        MoveWithin(appWindow.Position, new SizeInt32(Pixels(width) + size.Width - client.Width,
            Pixels(height) + size.Height - client.Height), area);
    }

    private void MoveWithin(PointInt32 destination, SizeInt32 size, RectInt32 area)
    {
        var appWindow = _window.AppWindow;
        size.Width = Math.Min(size.Width, area.Width);
        size.Height = Math.Min(size.Height, area.Height);
        var current = appWindow.Size;
        if (!size.Equals(current) &&
            !(_lastResize is { } last && last.Request.Equals(size) && last.Actual.Equals(current)))
        {
            var proposed = WorkAreaBounds.Clamp(destination, size, area);
            appWindow.MoveAndResize(new RectInt32(proposed.X, proposed.Y, size.Width, size.Height));
            // Windows 的最小边框高度可能高于请求值。用落地外框约束位置，并记住结果以免重复缩放。
            current = appWindow.Size;
            _lastResize = (size, current);
        }
        var point = WorkAreaBounds.Clamp(destination, current, area);
        if (!appWindow.Position.Equals(point))
            appWindow.Move(point);
    }

    private static string ThemePath => Path.Combine(AppProfile.DirectoryPath, "theme.txt");

    private static ElementTheme LoadTheme()
    {
        try
        {
            string saved = File.ReadAllText(ThemePath).Trim();
            if (saved == "dark") return ElementTheme.Dark;
            if (saved == "light") return ElementTheme.Light;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Application.Current.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
    }

    private void OnThemeSelected(object sender, SelectionChangedEventArgs args)
    {
        ApplyTheme();
        try
        {
            Directory.CreateDirectory(AppProfile.DirectoryPath);
            File.WriteAllText(ThemePath + ".tmp", _theme.SelectedIndex == 1 ? "dark" : "light");
            File.Move(ThemePath + ".tmp", ThemePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "主题已切换，本次无法保存设置。";
        }
    }

    private void ApplyTheme() => _root.RequestedTheme = _theme.SelectedIndex == 1 ? ElementTheme.Dark : ElementTheme.Light;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!IsBlank(args.OriginalSource) || !args.GetCurrentPoint(_root).Properties.IsLeftButtonPressed)
            return;
        if (args.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse ||
            !GetCursorPos(out var cursor) || !_root.CapturePointer(args.Pointer)) return;
        var position = _window.AppWindow.Position;
        _dragOffset = new PointInt32(cursor.X - position.X, cursor.Y - position.Y);
        _dragDpi = Math.Max(96u, GetDpiForWindow(_hwnd));
        _dragPointer = args.Pointer.PointerId;
        _dragTimer.Start();
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointer != args.Pointer.PointerId) return;
        if (GetAsyncKeyState(1 /* VK_LBUTTON */) >= 0)
        {
            EndDrag();
            return;
        }
        MoveWithCursor();
        args.Handled = true;
    }

    private void OnDragTick(DispatcherQueueTimer sender, object args)
    {
        if (!_dragPointer.HasValue) return;
        if (GetAsyncKeyState(1) < 0) MoveWithCursor();
        else EndDrag();
    }

    private void MoveWithCursor()
    {
        if (!GetCursorPos(out var current) || WorkAreaBounds.ForPoint(current) is not { } area) return;
        // 跨不同 DPI 的显示器时，抓住的图标或面板空白处也会随窗口缩放。
        double scale = Math.Max(96u, GetDpiForWindow(_hwnd)) / (double)_dragDpi;
        var destination = new PointInt32(
            current.X - (int)Math.Round(_dragOffset.X * scale),
            current.Y - (int)Math.Round(_dragOffset.Y * scale));
        // 由鼠标所在的屏幕决定边界；否则面板会被旧屏的边缘挡住，拖不到另一块屏幕。
        MoveWithin(destination, _window.AppWindow.Size, area);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointer != args.Pointer.PointerId) return;
        MoveWithCursor();
        EndDrag();
        args.Handled = true;
    }

    private void OnPointerEnded(object sender, PointerRoutedEventArgs args)
    {
        if (_dragPointer == args.Pointer.PointerId) EndDrag();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragPointer.HasValue) return;
        _dragPointer = null;
        _dragTimer.Stop();
        _root.ReleasePointerCaptures();
        SizeWindow();
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if ((_mini && _toolbarSettings.UsesIcons) || !IsBlank(args.OriginalSource)) return;
        args.Handled = true;
        _menu.ShowAt(_root, new FlyoutShowOptions { Position = args.GetPosition(_root) });
    }

    private bool IsBlank(object source)
    {
        for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            // 只把输入法工具栏的鱼篓区域当作拖动面，不吞掉中间三个按钮的点击。
            if (node == _root || node == _imeBar.DragGrip) return true;
            if (node is Control) return false;
        }
        return false;
    }

    private void UpdateFeedback()
    {
        bool visible = !string.IsNullOrWhiteSpace(_status.Text);
        ToolTipService.SetToolTip(_status, visible ? _status.Text : null);
        if (_mini && visible) SetMiniMode(false);
        if (visible == _feedbackVisible) return;
        _feedbackVisible = visible;
        _status.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!_mini) SizeWindow();
    }

    public void Dispose()
    {
        _disposed = true;
        RemoveWindowSubclass(_hwnd, _workAreaProc, WorkAreaSubclass);
        _window.AppWindow.Changed -= OnAppWindowChanged;
        EndDrag();
        _dragTimer.Tick -= OnDragTick;
        _theme.SelectionChanged -= OnThemeSelected;
        _root.PointerPressed -= OnPointerPressed;
        _root.PointerMoved -= OnPointerMoved;
        _root.PointerReleased -= OnPointerReleased;
        _root.PointerCanceled -= OnPointerEnded;
        _root.PointerCaptureLost -= OnPointerEnded;
        _window.Activated -= OnWindowActivated;
        _root.RightTapped -= OnRightTapped;
        _root.Loaded -= OnLoaded;
        if (_xamlRoot != null) _xamlRoot.Changed -= OnXamlRootChanged;
        _collapse.Click -= ToggleMode;
        _expand.Click -= ToggleMode;
        _imeBar.Expand.Click -= ToggleMode;
        _exit.Click -= RequestClose;
        _toolbarSettings.Changed -= OnToolbarSettingsChanged;
        _toolbar.Dispose();
        _status.UnregisterPropertyChangedCallback(TextBlock.TextProperty, _statusToken);
    }

    private delegate nint SubclassProc(nint hwnd, uint message, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wp, nint lp);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointInt32 point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
