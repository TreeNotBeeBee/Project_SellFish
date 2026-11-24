using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WindowPilot;

internal sealed class NativeTextWindow : DetachedWindow
{
    private readonly NativeControlBinding _binding;
    private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Content = "写回原控件" };
    private readonly Button _reload = new() { Content = "重新载入" };
    private readonly DispatcherQueueTimer _timer;
    private bool _busy, _closed;
    private string _baseText;
    private string _latest;
    private static string Normalized(string text) => text.ReplaceLineEndings("\n");

    internal NativeTextWindow(NativeControlBinding binding) : base(binding.Label + " · sell fish", 420, 250, binding.Writable)
    {
        _binding = binding;
        _text.IsReadOnly = !binding.Writable;
        _baseText = _latest = binding.InitialText;
        _text.Text = _baseText;
        Identify(_text, "NativeText", binding.Label);
        ScrollViewer.SetVerticalScrollBarVisibility(_text, ScrollBarVisibility.Auto);
        Layout.Children.Add(_text);
        if (binding.Writable)
        {
            var actions = AddActions();
            actions.Children.Add(_reload);
            actions.Children.Add(_apply);
            Identify(_apply, "ApplyNativeText", "写回原控件");
            Identify(_reload, "ReloadNativeText", "重新载入原文");
            _apply.Click += async (_, _) => await ApplyAsync();
            _reload.Click += (_, _) => { Load(_latest); SetStatus(""); };
            _text.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => UpdateActions());
            UpdateActions();
        }
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(800);
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        Closed += (_, _) => { _closed = true; _timer.Stop(); _binding.Dispose(); };
    }

    private bool Dirty => Normalized(_text.Text) != Normalized(_baseText);
    private void UpdateActions()
    {
        _apply.IsEnabled = !_busy && Dirty && _latest == _baseText;
        _reload.IsEnabled = !_busy && (Dirty || _latest != _baseText);
    }
    private void Load(string value)
    {
        _baseText = _latest = value;
        _text.Text = value;
        UpdateActions();
    }

    private async Task RefreshAsync()
    {
        if (_closed || _busy) return;
        _busy = true;
        try
        {
            string latest = await _binding.ReadAsync();
            if (_closed) return;
            _latest = latest;
            if (_latest != _baseText && !Dirty) Load(_latest);
            SetStatus(_latest != _baseText ? "原控件已更改。浮窗保留了你的文字，可复制后重新载入。" : "");
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _timer.Stop();
                _text.IsReadOnly = true;
                _apply.Visibility = _reload.Visibility = Visibility.Collapsed;
                SetStatus(ex.Message);
            }
        }
        finally { _busy = false; if (!_closed) UpdateActions(); }
    }

    private async Task ApplyAsync()
    {
        if (_closed || _busy || !Dirty) return;
        _busy = true;
        _text.IsReadOnly = true;
        UpdateActions();
        try
        {
            string replacement = _text.Text.ReplaceLineEndings(_baseText.Contains("\r\n") ? "\r\n" : "\n");
            string accepted = await _binding.WriteAsync(_baseText, replacement);
            if (!_closed) { Load(accepted); SetStatus(""); }
        }
        catch (Exception ex) { if (!_closed) SetStatus(ex.Message); }
        finally
        {
            _busy = false;
            if (!_closed) { _text.IsReadOnly = false; UpdateActions(); }
        }
    }
}
