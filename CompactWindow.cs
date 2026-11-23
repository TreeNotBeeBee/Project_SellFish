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

/// <summary>Captionless controller with normal system move, resize and minimize behavior.</summary>
internal sealed class CompactWindow : IDisposable
{
    private const int ContentWidth = 536;
    private const int ContentHeight = 154;
    private const int FeedbackHeight = 40;

    private readonly Window _window;
    private readonly Grid _root;
    private readonly ComboBox _theme;
    private readonly TextBlock _status;
    private readonly nint _hwnd;
    private readonly MenuFlyout _menu = new();
    private readonly DispatcherQueueTimer _dragTimer;
    private readonly long _statusToken;
    private bool _feedbackVisible;
    private uint? _dragPointer;
    private POINT _dragOrigin;
    private PointInt32 _windowOrigin;

    public CompactWindow(Window window, Grid root, ComboBox theme, TextBlock status)
    {
        _window = window;
        _root = root;
        _theme = theme;
        _status = status;
        _hwnd = WindowNative.GetWindowHandle(window);
        // A moving window can stay under the same local pointer coordinate. Sampling
        // screen coordinates while held also covers gaps in XAML PointerMoved events.
        _dragTimer = window.DispatcherQueue.CreateTimer();
        _dragTimer.Interval = TimeSpan.FromMilliseconds(16);
        _dragTimer.Tick += OnDragTick;

        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.PreferredMinimumWidth = Pixels(430);
            presenter.PreferredMinimumHeight = Pixels(ContentHeight);
        }
        var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        window.AppWindow.ResizeClient(new SizeInt32(Math.Min(Pixels(ContentWidth), area.Width), Math.Min(Pixels(ContentHeight), area.Height)));
        var size = window.AppWindow.Size;
        window.AppWindow.Move(new PointInt32(
            area.X + Math.Max(0, (area.Width - size.Width) / 2),
            area.Y + Math.Max(0, (area.Height - size.Height) / 2)));

        var minimize = new MenuFlyoutItem { Text = "最小化" };
        minimize.Click += (_, _) => (window.AppWindow.Presenter as OverlappedPresenter)?.Minimize();
        _menu.Items.Add(minimize);
        var exit = new MenuFlyoutItem { Text = "退出窗口轻控", KeyboardAcceleratorTextOverride = "Alt+F4" };
        exit.Click += (_, _) => window.Close();
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
        _statusToken = status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateFeedback());
        UpdateFeedback();
    }

    private int Pixels(double dips) => (int)Math.Round(dips * Math.Max(96, GetDpiForWindow(_hwnd)) / 96);

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
        if (visible == _feedbackVisible) return;
        _feedbackVisible = visible;
        _status.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var size = _window.AppWindow.ClientSize;
        int delta = Pixels(FeedbackHeight) * (visible ? 1 : -1);
        int height = Math.Max(Pixels(ContentHeight), size.Height + delta);
        _window.AppWindow.ResizeClient(new SizeInt32(size.Width, height));
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
        _status.UnregisterPropertyChangedCallback(TextBlock.TextProperty, _statusToken);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
}
