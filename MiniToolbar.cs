using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WindowPilot;

/// <summary>One available mini-toolbar command, sharing the main panel's state and action.</summary>
internal sealed record MiniToggle(string Id, CheckBox Source, Action<bool> Apply, string IconKey);

internal sealed class MiniToolbar : IDisposable
{
    private sealed class ToggleView : IDisposable
    {
        private readonly MiniToggle _action;
        private readonly long _checkedToken;
        private readonly long _enabledToken;
        public ToggleButton Button { get; }

        public ToggleView(MiniToggle action, Style style, DataTemplate? icon)
        {
            _action = action;
            Button = icon != null
                ? new ToggleButton { Content = icon.LoadContent() }
                : new CheckBox { Content = action.Source.Content };
            Button.Style = style;
            AutomationProperties.SetAutomationId(Button, $"Mini.{action.Id}");
            var label = action.Source.Content?.ToString() ?? action.Id;
            AutomationProperties.SetName(Button, label);
            ToolTipService.SetToolTip(Button, label);
            _checkedToken = action.Source.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, Update);
            _enabledToken = action.Source.RegisterPropertyChangedCallback(Control.IsEnabledProperty, Update);
            Button.Click += OnClick;
            Sync();
        }

        private void Update(DependencyObject sender, DependencyProperty property) => Sync();

        private void Sync()
        {
            Button.IsChecked = _action.Source.IsChecked;
            Button.IsEnabled = _action.Source.IsEnabled;
        }

        private void OnClick(object sender, RoutedEventArgs args)
        {
            try { _action.Apply(Button.IsChecked == true); }
            finally { Sync(); }
        }

        public void Dispose()
        {
            Button.Click -= OnClick;
            _action.Source.UnregisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, _checkedToken);
            _action.Source.UnregisterPropertyChangedCallback(Control.IsEnabledProperty, _enabledToken);
        }
    }

    private readonly Dictionary<string, MiniToggle> _actions;
    private readonly List<ToggleView> _views = [];
    private StackPanel? _host;

    public MiniToolbar(IEnumerable<MiniToggle> actions) =>
        _actions = actions.ToDictionary(action => action.Id, StringComparer.OrdinalIgnoreCase);

    public void Show(StackPanel host, IReadOnlyList<string> ids, Style style, ResourceDictionary? icons)
    {
        Clear();
        _host = host;
        foreach (string id in ids)
        {
            var action = _actions[id];
            var view = new ToggleView(action, style, icons == null ? null : (DataTemplate)icons[action.IconKey]);
            _views.Add(view);
            host.Children.Add(view.Button);
        }
    }

    public void Clear()
    {
        // 切换外观或展开面板时，同时解绑旧按钮和它对应的状态订阅。
        foreach (var view in _views) view.Dispose();
        _views.Clear();
        _host?.Children.Clear();
        _host = null;
    }

    public void Dispose() => Clear();
}
