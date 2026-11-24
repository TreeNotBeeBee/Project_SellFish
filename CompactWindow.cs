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
    private const int ContentHeight = 223;
    private const int FeedbackHeight = 40;

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
    private readonly MenuFlyoutItem _switchMode = new();
    private readonly nint _hwnd;
    private readonly MenuFlyout _menu = new();
    private readonly DispatcherQueueTimer _dragTimer;
    private readonly long _statusToken;
    private bool _feedbackVisible;
    private bool _mini;
    private XamlRoot? _xamlRoot;
    private uint _sizeDpi;
    private uint? _dragPointer;
    private POINT _dragOrigin;
    private PointInt32 _windowOrigin;

    public CompactWindow(Window window, Grid root, ComboBox theme, TextBlock status,
        MiniToggle[] actions)
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
        _toolbar = new MiniToolbar(_miniActions, actions, (Style)root.Resources["ToolbarToggle"]);
        // A moving window can stay under the same local pointer coordinate. Sampling
        // screen coordinates while held also covers gaps in XAML PointerMoved events.
        _dragTimer = window.DispatcherQueue.CreateTimer();
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
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = window.AppWindow.Size;
        window.AppWindow.Move(new PointInt32(
            area.X + Math.Max(0, (area.Width - size.Width) / 2),
            area.Y + Math.Max(0, (area.Height - size.Height) / 2)));

        _collapse.Click += ToggleMode;
        _expand.Click += ToggleMode;
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

    private void SetMiniMode(bool mini)
    {
        if (_mini == mini) return;
        _mini = mini;
        if (mini) _toolbar.Reload();
        _fullPanel.Visibility = _collapse.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        _miniActions.Visibility = _expand.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        _caption.VerticalAlignment = mini ? VerticalAlignment.Center : VerticalAlignment.Top;
        _caption.Margin = new Thickness(0, mini ? 0 : 2, 3, 0);
        _switchMode.Text = mini ? "展开主面板" : "收起为工具栏";
        SizeWindow();
        (mini ? _expand : _collapse).Focus(FocusState.Programmatic);
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
        _sizeDpi = Math.Max(96u, GetDpiForWindow(_hwnd));
        var appWindow = _window.AppWindow;
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double width = ContentWidth;
        double height = ContentHeight + (_feedbackVisible ? FeedbackHeight : 0);
        if (_mini)
        {
            var unlimited = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
            _miniActions.Measure(unlimited);
            _caption.Measure(unlimited);
            width = _miniActions.DesiredSize.Width + _caption.DesiredSize.Width;
            height = Math.Max(36, Math.Max(_miniActions.DesiredSize.Height, _caption.DesiredSize.Height));
        }
        var requested = new SizeInt32(Math.Min(Pixels(width), area.Width), Math.Min(Pixels(height), area.Height));
        var currentSize = appWindow.ClientSize;
        if (requested.Width != currentSize.Width || requested.Height != currentSize.Height)
        {
            // With a custom caption ResizeClient adds the stock caption height on Windows 10.
            // Use the live nonclient dimensions, which reflect this HWND's actual frame.
            var frame = appWindow.Size;
            appWindow.Resize(new SizeInt32(requested.Width + frame.Width - currentSize.Width,
                requested.Height + frame.Height - currentSize.Height));
        }
        var position = appWindow.Position;
        var size = appWindow.Size;
        int x = Math.Clamp(position.X, area.X, Math.Max(area.X, area.X + area.Width - size.Width));
        int y = Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - size.Height));
        if (x != position.X || y != position.Y) appWindow.Move(new PointInt32(x, y));
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
            !GetCursorPos(out _dragOrigin) || !_root.CapturePointer(args.Pointer)) return;
        _windowOrigin = _window.AppWindow.Position;
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
        if (!GetCursorPos(out POINT current)) return;
        var destination = new PointInt32(
            _windowOrigin.X + current.X - _dragOrigin.X,
            _windowOrigin.Y + current.Y - _dragOrigin.Y);
        var position = _window.AppWindow.Position;
        if (position.X != destination.X || position.Y != destination.Y)
            _window.AppWindow.Move(destination);
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
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs args)
    {
        if (!IsBlank(args.OriginalSource)) return;
        args.Handled = true;
        _menu.ShowAt(_root, new FlyoutShowOptions { Position = args.GetPosition(_root) });
    }

    private bool IsBlank(object source)
    {
        for (var node = source as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == _root) return true;
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
        _exit.Click -= RequestClose;
        _toolbar.Dispose();
        _status.UnregisterPropertyChangedCallback(TextBlock.TextProperty, _statusToken);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool PostMessageW(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
