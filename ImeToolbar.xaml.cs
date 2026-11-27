using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WindowPilot;

public sealed partial class ImeToolbar : UserControl
{
    private bool _inputMethod;

    public ImeToolbar() => InitializeComponent();

    internal void SetInputMethodSkin(bool inputMethod)
    {
        if (_inputMethod == inputMethod) return;
        _inputMethod = inputMethod;
        Strip.Padding = inputMethod ? new Thickness(2, 0, 2, 0) : new Thickness(3, 2, 3, 2);
        Strip.Background = (Brush)Resources[inputMethod ? "KeyboardSurfaceBrush" : "ImeSurfaceBrush"];
        DragGrip.Width = inputMethod ? 27 : 28;
        DragGrip.Height = Expand.Height = inputMethod ? 22 : 24;
        Expand.Width = inputMethod ? 23 : 28;
        Expand.Foreground = (Brush)Resources[inputMethod ? "KeyboardInkBrush" : "ImeInkBrush"];
        Expand.Content = ((DataTemplate)Resources[inputMethod ? "KeyboardExpand" : "InitialExpand"]).LoadContent();
        Logo.Width = inputMethod ? 22 : 21;
        Logo.Height = inputMethod ? 20 : 21;
        Logo.Source = inputMethod
            ? new SvgImageSource(new Uri("ms-appx:///Assets/InputMethodMark.svg"))
            : new BitmapImage(new Uri("ms-appx:///Assets/FishBasket.png"));
        AutomationProperties.SetHelpText(Logo, inputMethod ? "输入法图标" : "初始");
    }
}
