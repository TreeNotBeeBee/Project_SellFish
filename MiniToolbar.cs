using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WindowPilot;

/// <summary>One available mini-toolbar command, sharing the main panel's state and action.</summary>
internal sealed record MiniToggle(string Id, CheckBox Source, Action<bool> Apply, string IconKey);

internal sealed class MiniToolbar : IDisposable
{
    // 输入法图案固定在各自的位置，实际操作和无障碍名称仍由该位置分配的功能提供。
    private static readonly string[] KeyboardIcons = ["KeyboardLanguage", "KeyboardPunctuation", "KeyboardInput", "KeyboardMicrophone"];

    private sealed class ToggleView : IDisposable
    {
        private readonly MiniToggle _action;
        private readonly long _checkedToken;
        private readonly long _enabledToken;
        public ToggleButton Button { get; }

        public ToggleView(MiniToggle action, Style style, DataTemplate icon)
        {
            _action = action;
            Button = new ToggleButton { Style = style, Content = icon.LoadContent() };
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

    public void Show(ImeToolbar bar, IReadOnlyList<string> ids, bool inputMethod)
    {
        Clear();
        _host = bar.Actions;
        bar.SetInputMethodSkin(inputMethod);
        _host.Visibility = ids.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        var style = (Style)bar.Resources[inputMethod ? "KeyboardToggle" : "ImeToggle"];
        for (int position = 0; position < ids.Count; position++)
        {
            var action = _actions[ids[position]];
            var iconKey = inputMethod ? KeyboardIcons[position] : action.IconKey;
            var view = new ToggleView(action, style, (DataTemplate)bar.Resources[iconKey]);
            _views.Add(view);
            _host.Children.Add(view.Button);
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
