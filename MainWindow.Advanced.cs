using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WindowPilot;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, NativeTextWindow> _detached = [];
    private CancellationTokenSource? _picking;
    private bool _pinned;
    private static string PinPath => Path.Combine(AppProfile.DirectoryPath, "tool-topmost.txt");

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
        SettingsPage.Visibility = sender.SelectedItem == SettingsTab ? Visibility.Visible : Visibility.Collapsed;
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
        PickControl.IsEnabled = unavailable == null && _picking == null;
        string hint = unavailable ?? (_picking != null
            ? "单击目标窗口内的文本控件，按 Esc 取消。"
            : "可从目标窗口选择文本或输入框。");
        if (AdvancedHint.Text != hint) AdvancedHint.Text = hint;
    }

    private async void PickNativeControl(object sender, RoutedEventArgs args)
    {
        if (_windows.Target is not { } target || _picking != null) return;
        using var cancellation = new CancellationTokenSource();
        _picking = cancellation;
        RefreshAdvancedControls();
        try
        {
            var binding = await NativeControlPicker.PickAsync(target, cancellation.Token);
            if (cancellation.IsCancellationRequested || _windows.Target != target || !_windows.IsAlive())
            { binding.Dispose(); return; }
            if (_detached.TryGetValue(binding.Key, out var existing))
            {
                binding.Dispose();
                existing.Activate();
                return;
            }
            NativeTextWindow view;
            try { view = new NativeTextWindow(binding); }
            catch { binding.Dispose(); throw; }
            ShowDetached(view, binding.Key, binding.Label);
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

    private void ShowDetached(NativeTextWindow window, string key, string label)
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
