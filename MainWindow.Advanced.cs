using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowPilot;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, DetachedWindow> _detached = [];
    private CancellationTokenSource? _picking;
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
        ShortcutsPage.Visibility = sender.SelectedItem == ShortcutsTab ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = sender.SelectedItem == SettingsTab ? Visibility.Visible : Visibility.Collapsed;
        if (sender.SelectedItem != SettingsTab)
        {
            ToolbarSettingsPage.Visibility = Visibility.Collapsed;
            SettingsOverview.Visibility = Visibility.Visible;
        }
        if (sender.SelectedItem != BasicTab) _windowPicking?.Cancel();
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
        foreach (var view in _detached.Values) view.SetAppearance(_pinned, Root.RequestedTheme);
    }

    private void RefreshAdvancedControls()
    {
        string? unavailable = _windows.Target == null ? "先到「基础」绑定要操作的窗口。" :
            _windows.IsHidden ? "先显示目标窗口，再选择控件。" :
            _windows.IsClickThrough ? "先关闭目标窗口的页面穿透，再选择控件。" :
            _requestedOpacity == 0 ? "先调高目标窗口的不透明度，再选择控件。" : null;
        if (unavailable != null) _picking?.Cancel();
        PickControl.IsEnabled = PickRegion.IsEnabled = unavailable == null && _picking == null;
        string hint = unavailable ?? (_picking != null
            ? "单击目标窗口中的文字或控件，按 Esc 取消。"
            : "选择控件后，点输入框可编辑，点网页正文可跟随阅读。");
        if (AdvancedHint.Text != hint) AdvancedHint.Text = hint;
    }

    private async void PickNativeControl(object sender, RoutedEventArgs args) => await PickControlAsync(outputRegion: false);
    private async void PickOutputRegion(object sender, RoutedEventArgs args) => await PickControlAsync(outputRegion: true);

    private async Task PickControlAsync(bool outputRegion)
    {
        if (_windows.Target is not { } target || _picking != null) return;
        using var cancellation = new CancellationTokenSource();
        _picking = cancellation;
        RefreshAdvancedControls();
        try
        {
            var source = await NativeControlPicker.PickAsync(target, cancellation.Token, outputRegion);
            // 点选期间可以释放或更换目标。只有原绑定仍有效，才把新来源交给浮窗。
            if (cancellation.IsCancellationRequested || _windows.Target != target || !_windows.IsAlive())
            { source.Dispose(); return; }
            if (_detached.TryGetValue(source.Key, out var open))
            { source.Dispose(); open.Activate(); Status(""); return; }
            DetachedWindow view;
            try
            {
                view = source switch
                {
                    NativeTextRegion region => new NativeRegionWindow(region),
                    NativeControlBinding binding => new NativeTextWindow(binding),
                    _ => throw new InvalidOperationException("不支持的控件类型。")
                };
            }
            catch { source.Dispose(); throw; }
            ShowDetached(view, source.Key, source.Label);
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

    private void ShowDetached(DetachedWindow window, string key, string label)
    {
        string initialTitle = window.Title;
        int suffix = 1;
        while (_detached.Values.Any(w => w.Title == window.Title))
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
        _detached.Add(key, window);
        DetachedRows.Children.Add(row);
        window.Closed += (_, _) => { _detached.Remove(key); DetachedRows.Children.Remove(row); };
        try { window.SetAppearance(_pinned, Root.RequestedTheme); window.Show(); }
        catch { window.Close(); throw; }
    }

    private void EndDetachedSession()
    {
        _picking?.Cancel();
        foreach (var view in _detached.Values.ToArray()) view.Close();
    }
}
