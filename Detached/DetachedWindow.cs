using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace WindowPilot;

internal abstract class DetachedWindow : Window
{
    protected readonly Grid Layout = new() { Padding = new Thickness(12) };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed
    };
    private readonly nint _hwnd;

    protected DetachedWindow(string title, int width, int height, bool lower)
    {
        Title = title;
        _hwnd = WindowNative.GetWindowHandle(this);
        Layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_status, 1);
        Grid.SetColumnSpan(_status, 2);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        Layout.Children.Add(_status);
        Content = Layout;
        Layout.ActualThemeChanged += (_, _) => ApplyBackground();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "FishBasket.ico"));
        // A tool window is absent from the taskbar and Alt+Tab, but remains normally editable.
        int style = GetWindowLongW(_hwnd, -20);
        SetWindowLongW(_hwnd, -20, (style | 0x80) & ~0x40000);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double scale = Math.Max(1, GetDpiForWindow(_hwnd) / 96.0);
        int w = Math.Min(area.Width, (int)Math.Round(width * scale));
        int h = Math.Min(area.Height, (int)Math.Round(height * scale));
        int gap = (int)Math.Round(28 * scale);
        int x = Math.Max(area.X, area.X + area.Width - w - gap);
        int y = lower ? Math.Max(area.Y, area.Y + area.Height - h - gap) : Math.Min(area.Y + area.Height - h, area.Y + gap);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        ApplyBackground();
    }

    private void ApplyBackground()
    {
        bool dark = Layout.ActualTheme == ElementTheme.Dark;
        Layout.Background = new SolidColorBrush(dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
        int attr = dark ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, 20, ref attr, sizeof(int));
    }

    internal void SetAppearance(bool topmost, ElementTheme theme)
    {
        Layout.RequestedTheme = theme;
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop != topmost)
            presenter.IsAlwaysOnTop = topmost;
    }

    internal void Show() => AppWindow.Show(false);

    internal void SetStatus(string message)
    {
        if (_status.Text == message) return;
        _status.Text = message;
        _status.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    protected static void Identify(FrameworkElement element, string id, string label)
    {
        AutomationProperties.SetAutomationId(element, id);
        AutomationProperties.SetName(element, label);
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLongW(nint hwnd, int index, int value);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
}
