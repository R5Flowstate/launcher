using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace R5Flowstate.Shell;

/// <summary>XAML: Text="{loc:T play}". Updates when the UI language changes.</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension(string key)
    {
        Key = key;
    }

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };

        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget pvt
            && pvt.TargetObject is DependencyObject dobj
            && pvt.TargetProperty is DependencyProperty dp)
        {
            BindingOperations.SetBinding(dobj, dp, binding);
            return binding.ProvideValue(serviceProvider);
        }

        return binding.ProvideValue(serviceProvider);
    }
}
