using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WindowPilot;

/// <summary>One available mini-toolbar command, sharing the main panel's state and action.</summary>
internal sealed record MiniToggle(string Id, CheckBox Source, Action<bool> Apply);

internal sealed class MiniToolbar : IDisposable
{
    private sealed class ToggleView : IDisposable
    {
        private readonly MiniToggle _action;
        private readonly long _checkedToken;
        private readonly long _enabledToken;
        public CheckBox Button { get; }

        public ToggleView(MiniToggle action, Style style)
        {
            _action = action;
            Button = new CheckBox { Style = style, Content = action.Source.Content };
            AutomationProperties.SetAutomationId(Button, $"Mini.{action.Id}");
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

    private readonly StackPanel _host;
    private readonly string[] _defaults;
    private readonly Dictionary<string, ToggleView> _views;

    public MiniToolbar(StackPanel host, IReadOnlyList<MiniToggle> actions, Style style)
    {
        _host = host;
        _defaults = actions.Select(action => action.Id).ToArray();
        _views = actions.ToDictionary(action => action.Id, action => new ToggleView(action, style),
            StringComparer.OrdinalIgnoreCase);
        Reload();
    }

    public void Reload()
    {
        var ids = LoadIds();
        _host.Children.Clear();
        foreach (string id in ids) _host.Children.Add(_views[id].Button);
    }

    private string[] LoadIds()
    {
        string path = Path.Combine(AppProfile.DirectoryPath, "mini-toolbar.json");
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(AppProfile.DirectoryPath);
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                JsonSerializer.Serialize(stream, _defaults, new JsonSerializerOptions { WriteIndented = true });
                return _defaults;
            }

            var configured = JsonSerializer.Deserialize<string?[]>(File.ReadAllText(path));
            if (configured is null) return _defaults;
            var available = configured.OfType<string>().Where(_views.ContainsKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return configured.Length > 0 && available.Length == 0 ? _defaults : available;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return _defaults;
        }
    }

    public void Dispose()
    {
        foreach (var view in _views.Values) view.Dispose();
    }
}
