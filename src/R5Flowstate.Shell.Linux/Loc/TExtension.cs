using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using R5Flowstate.Shell.Linux;

namespace R5Flowstate.Shell.Linux;

/// <summary>XAML: Text="{loc:T play}". Updates when the UI language changes.
/// Avalonia port of the WPF TExtension. Avalonia has no IMarkupExtension — the
/// XAML compiler duck-types ProvideValue — so this returns a one-way reflection
/// binding to the Loc indexer; language changes refresh every bound string.</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
    }
}