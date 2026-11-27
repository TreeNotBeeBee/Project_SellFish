using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace WindowPilot;

public sealed partial class MainWindow
{
    private static readonly Color DefaultKeyColor = Color.FromArgb(255, 0, 0, 255);
    private readonly PercentageInput _keyRangeInput;
    private readonly PercentageInput _keyStrengthInput;
    private CancellationTokenSource? _colorPicking;
    private int _keyRange;
    private int _keyStrength = 100;

    private int EffectiveKeyStrength => KeyEnabled.IsChecked == true ? _keyStrength : 0;
    private bool _pinned;
    private static string PinPath => Path.Combine(AppProfile.DirectoryPath, "tool-topmost.txt");

    private void InitializeToolbarSettings(MiniToolbarSettings settings, MiniToggle[] actions)
    {
        var boxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        int columns = ToolbarActionOptions.ColumnDefinitions.Count;
        for (int i = 0; i < actions.Length; i++)
        {
            var action = actions[i];
            var box = new CheckBox
            {
                Content = action.Source.Content as string ?? action.Id,
                Style = (Style)Root.Resources["PanelCheckBox"]
            };
            AutomationProperties.SetAutomationId(box, $"ToolbarAction.{action.Id}");
            boxes.Add(action.Id, box);
            if (i % columns == 0)
                ToolbarActionOptions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(box, i / columns);
            Grid.SetColumn(box, i % columns);
            ToolbarActionOptions.Children.Add(box);
            box.Click += (_, _) => Save(() => settings.SetSelected(action.Id, box.IsChecked == true));
        }

        void Refresh()
        {
            var selected = settings.Slots;
            MiniStyleBox.SelectedIndex = settings.Style == MiniToolbarStyle.InputMethod ? 1 : 0;
            foreach (var (id, box) in boxes)
            {
                bool chosen = selected.Contains(id);
                box.IsChecked = chosen;
                box.IsEnabled = chosen || selected.Count < MiniToolbarSettings.MaxActions;
            }
            ToolbarCount.Text = $"已选 {selected.Count} / {MiniToolbarSettings.MaxActions}";
            ToolbarOrder.Text = selected.Count == 0 ? "未选择功能" :
                string.Join(" → ", selected.Select(id => (string)boxes[id].Content));
        }

        void Save(Action change)
        {
            Run(() => { change(); Status(""); });
            // 写盘失败时复选框也要回到上一次成功保存的选择及顺序。
            Refresh();
        }

        Refresh();
        MiniStyleBox.SelectionChanged += (_, _) =>
        {
            if (MiniStyleBox.SelectedIndex < 0) return;
            var style = MiniStyleBox.SelectedIndex == 0 ? MiniToolbarStyle.InitialIcons : MiniToolbarStyle.InputMethod;
            if (style != settings.Style) Save(() => settings.SetStyle(style));
        };
    }

    private void OpenToolbarSettings(object sender, RoutedEventArgs args)
    {
        SettingsOverview.Visibility = Visibility.Collapsed;
        ToolbarSettingsPage.Visibility = Visibility.Visible;
        ToolbarSettingsBack.Focus(FocusState.Programmatic);
    }

    private void CloseToolbarSettings(object sender, RoutedEventArgs args)
    {
        ToolbarSettingsPage.Visibility = Visibility.Collapsed;
        SettingsOverview.Visibility = Visibility.Visible;
        EditToolbarActions.Focus(FocusState.Programmatic);
    }

    private void InitializeToolPin()
    {
        try { _pinned = File.Exists(PinPath) && File.ReadAllText(PinPath).Trim() == "1"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Status("无法读取面板置顶设置。"); }
        ToolPin.IsChecked = _pinned;
        ApplyToolPin();
    }

    private void PageChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!_ready) return;
        BasicPage.Visibility = sender.SelectedItem == BasicTab ? Visibility.Visible : Visibility.Collapsed;
        AdvancedPage.Visibility = sender.SelectedItem == AdvancedTab ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = sender.SelectedItem == ShortcutsTab ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender.SelectedItem == SettingsTab ? Visibility.Visible : Visibility.Collapsed;
        if (sender.SelectedItem != SettingsTab)
        {
            ToolbarSettingsPage.Visibility = Visibility.Collapsed;
            SettingsOverview.Visibility = Visibility.Visible;
        }
        if (sender.SelectedItem != BasicTab) _windowPicking?.Cancel();
        if (sender.SelectedItem != AdvancedTab) _colorPicking?.Cancel();
    }

    private void SetToolPin(object sender, RoutedEventArgs args) => Run(() =>
    {
        _pinned = ToolPin.IsChecked == true;
        ApplyToolPin();
        Directory.CreateDirectory(AppProfile.DirectoryPath);
        File.WriteAllText(PinPath + ".tmp", _pinned ? "1" : "0");
        File.Move(PinPath + ".tmp", PinPath, true);
        Status("");
    });

    private void ApplyToolPin()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.IsAlwaysOnTop != _pinned)
            presenter.IsAlwaysOnTop = _pinned;
    }

    private void RefreshColorKeyControls(bool supported)
    {
        if (!supported) _colorPicking?.Cancel();
        bool editing = supported && _colorPicking == null;
        KeyEnabled.IsEnabled = KeyColorButton.IsEnabled = KeyEyedropper.IsEnabled = editing;
        KeyRangeInput.IsEnabled = KeyStrengthInput.IsEnabled = editing;
        string hint = _windows.Target == null ? "先到「基础」绑定要过滤颜色的窗口。" :
            !supported ? "此窗口不支持颜色过滤。" :
            _colorPicking != null ? "单击屏幕取色，按 Esc 取消。请选取目标的原始颜色。" :
            "按窗口原色匹配，调大颜色范围可包含近似色。强度 100% 时，匹配处显示下方画面。";
        if (AdvancedHint.Text != hint) AdvancedHint.Text = hint;
    }

    private void ShowKeyColor(Color color)
    {
        KeySwatchBrush.Color = color;
        KeyHex.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        AutomationProperties.SetName(KeyColorButton, "选择要过滤的颜色 " + KeyHex.Text);
    }

    private void ToggleColorKey(object sender, RoutedEventArgs args)
    {
        if (_ready && !_updating && _windows.Target != null && KeyEnabled.IsEnabled)
            Run(ApplyAppearance);
    }

    private void SetKeyRange(int value)
    {
        _keyRange = value;
        if (EffectiveKeyStrength > 0) ApplyAppearance();
        Status("");
    }

    private void SetKeyStrength(int value)
    {
        _keyStrength = value;
        if (KeyEnabled.IsChecked == true) ApplyAppearance();
        Status("");
    }

    private async void PickScreenColor(object sender, RoutedEventArgs args)
    {
        if (!_ready || _colorPicking != null || !KeyEyedropper.IsEnabled || _windows.Target is not { } target) return;
        using var cancellation = new CancellationTokenSource();
        _colorPicking = cancellation;
        try
        {
            SyncControls();
            Status("");
            var color = await ScreenPicker.PickColorAsync(cancellation.Token);
            // 取色可跨屏、跨窗口。等待点击期间，原绑定可能已释放、关闭或切换。
            if (_ready && !cancellation.IsCancellationRequested && _windows.Target == target && _windows.IsAlive())
                KeyColorPicker.Color = color;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (_ready && !cancellation.IsCancellationRequested) Status(ex.Message); }
        finally
        {
            _colorPicking = null;
            if (_ready) Run(SyncControls);
        }
    }
}
