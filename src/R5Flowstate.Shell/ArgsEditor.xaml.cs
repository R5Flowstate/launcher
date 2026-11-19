using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using R5Flowstate.Spawn;

namespace R5Flowstate.Shell;

/// <summary>
/// One launch line, editable: core arguments read-only, every other argument the
/// launcher builds as a checkbox, plus free-form extras.
/// </summary>
public partial class ArgsEditor : UserControl
{
    readonly HashSet<string> _disabled = new(StringComparer.OrdinalIgnoreCase);
    bool _ready;

    public event Action? Changed;

    public ArgsEditor()
    {
        InitializeComponent();
    }

    public string Extra => TxtExtra.Text ?? string.Empty;

    public string DisabledKeys => string.Join(' ', _disabled.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyCollection<string> ParseKeys(string? keys) =>
        string.IsNullOrWhiteSpace(keys)
            ? Array.Empty<string>()
            : keys.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <param name="generated">The launcher's own line, built with nothing unticked and no extras.</param>
    public void Load(string extra, string disabledKeys, IReadOnlyList<string> generated,
        IReadOnlySet<string> core, string applied)
    {
        _ready = false;
        _disabled.Clear();
        foreach (var k in ParseKeys(disabledKeys))
            _disabled.Add(k);
        TxtExtra.Text = extra ?? string.Empty;

        var entries = LaunchArgs.SplitEntries(generated);
        var coreText = string.Join(' ', entries.Where(e => core.Contains(e.Key)).Select(e => e.Text));
        TxtCore.Text = coreText.Length > 0 ? coreText : "-";

        PanelArgs.Children.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (core.Contains(entry.Key) || !seen.Add(entry.Key))
                continue;
            var box = new CheckBox
            {
                Content = new TextBlock { Text = entry.Text },
                Tag = entry.Key,
                IsChecked = !_disabled.Contains(entry.Key),
                Style = (Style)FindResource("QuietCheck"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 16, 8),
            };
            box.Checked += OnArgToggled;
            box.Unchecked += OnArgToggled;
            PanelArgs.Children.Add(box);
        }

        TxtApplied.Text = applied ?? string.Empty;
        _ready = true;
    }

    public void SetApplied(string applied) => TxtApplied.Text = applied ?? string.Empty;

    void OnArgToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not CheckBox { Tag: string key } box)
            return;
        if (box.IsChecked == true)
            _disabled.Remove(key);
        else
            _disabled.Add(key);
        Changed?.Invoke();
    }

    void OnExtraChanged(object sender, TextChangedEventArgs e)
    {
        if (_ready)
            Changed?.Invoke();
    }
}
