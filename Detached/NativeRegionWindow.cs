using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace WindowPilot;

internal sealed class NativeRegionWindow : DetachedWindow
{
    private readonly NativeTextRegion _region;
    private readonly DispatcherQueue _dispatcher;
    private readonly ComboBox _scope = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly RichTextBlock _output = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly Run _body = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly DispatcherQueueTimer _updates;
    private readonly DispatcherQueueTimer _fallback;
    private int _queued, _revision;
    private bool _reading, _dirty, _switching, _closed;
    private string _latest = "";

    internal NativeRegionWindow(NativeTextRegion region) : base("输出区域 · sell fish", 490, 380, false)
    {
        _region = region;
        _dispatcher = DispatcherQueue;
        var grid = new Grid { RowSpacing = 8 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        _scope.ItemsSource = region.Scopes;
        _scope.SelectedIndex = region.SelectedIndex;
        _scope.Header = "读取范围";
        Identify(_scope, "RegionScope", "读取范围");
        grid.Children.Add(_scope);
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(_body);
        _output.Blocks.Add(paragraph);
        Identify(_output, "RegionText", "输出内容");
        _scroll.Content = _output;
        Grid.SetRow(_scroll, 1);
        grid.Children.Add(_scroll);
        Layout.Children.Add(grid);

        _updates = DispatcherQueue.CreateTimer();
        _updates.Interval = TimeSpan.FromMilliseconds(120);
        _updates.IsRepeating = false;
        _updates.Tick += async (_, _) => await RefreshAsync();
        _fallback = DispatcherQueue.CreateTimer();
        _fallback.Interval = TimeSpan.FromSeconds(1);
        _fallback.Tick += (_, _) => ScheduleRead();
        _fallback.Start();
        _latest = region.InitialText;
        Render();
        _scope.SelectionChanged += async (_, _) => await ChangeScopeAsync();
        _output.SelectionChanged += (_, _) =>
        {
            if (_output.SelectedText.Length == 0) DispatcherQueue.TryEnqueue(() => Render());
        };
        _region.Changed += SourceChanged;
        ScheduleRead();
        Closed += (_, _) =>
        {
            _closed = true;
            _updates.Stop();
            _fallback.Stop();
            _region.Changed -= SourceChanged;
            _region.Dispose();
        };
    }

    private void SourceChanged()
    {
        // UIA 可能从后台线程为同一个词触发多次事件，只向界面排入一次读取。
        if (Interlocked.Exchange(ref _queued, 1) == 0)
            _dispatcher.TryEnqueue(() => { Interlocked.Exchange(ref _queued, 0); ScheduleRead(); });
    }

    private void ScheduleRead()
    {
        if (_closed) return;
        _dirty = true;
        if (!_reading && !_switching && !_updates.IsRunning) _updates.Start();
    }

    private async Task RefreshAsync()
    {
        if (_closed || _reading || _switching || !_dirty) return;
        int revision = _revision;
        _dirty = false;
        _reading = true;
        try
        {
            string text = await _region.ReadAsync();
            if (_closed || revision != _revision) return;
            _latest = text;
            Render();
            ShowReadStatus();
        }
        catch (Exception ex) { if (!_closed && revision == _revision) SetStatus(ex.Message); }
        finally { _reading = false; if (_dirty) ScheduleRead(); }
    }

    private async Task ChangeScopeAsync()
    {
        if (_closed || _switching || _scope.SelectedIndex < 0) return;
        _switching = true;
        _scope.IsEnabled = false;
        _revision++;
        try
        {
            string text = await _region.SelectScopeAsync(_scope.SelectedIndex);
            if (_closed) return;
            _latest = text;
            Render(changeScope: true);
            ShowReadStatus();
        }
        catch (Exception ex)
        {
            if (!_closed) { _scope.SelectedIndex = _region.SelectedIndex; SetStatus(ex.Message); }
        }
        finally { _switching = false; if (!_closed) { _scope.IsEnabled = true; ScheduleRead(); } }
    }

    private void ShowReadStatus() => SetStatus(_latest.Length == 0 ? "等待区域出现可读文字，也可以切换读取范围。" : "");

    private void Render(bool changeScope = false)
    {
        if (_closed || (!changeScope && (_body.Text == _latest || _output.SelectedText.Length != 0))) return;
        // 在底部阅读才跟随新增文字。复制期间保留选区，松开选区后再呈现最新文本。
        double position = _scroll.VerticalOffset;
        bool follow = _scroll.ScrollableHeight - position < 4;
        _body.Text = _latest;
        _scroll.UpdateLayout();
        _scroll.ChangeView(null, changeScope || follow ? _scroll.ScrollableHeight : position, null, true);
    }
}
