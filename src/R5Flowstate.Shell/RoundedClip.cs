using System.Windows;
using System.Windows.Media;

namespace R5Flowstate.Shell;

/// <summary>
/// Rounds an element's children to match a Border's CornerRadius.
///
/// A Border's CornerRadius shapes only the border and its own Background --
/// child content is not clipped by it, and ClipToBounds clips to the bounds
/// RECTANGLE, corners included. So an Image inside a rounded Border renders
/// square over it. Setting a rounded Clip on the content is the fix, and the
/// geometry has to be rebuilt whenever the element resizes.
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty RadiusProperty =
        DependencyProperty.RegisterAttached(
            "Radius", typeof(double), typeof(RoundedClip),
            new PropertyMetadata(0d, OnRadiusChanged));

    public static void SetRadius(DependencyObject o, double value) => o.SetValue(RadiusProperty, value);
    public static double GetRadius(DependencyObject o) => (double)o.GetValue(RadiusProperty);

    private static void OnRadiusChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not FrameworkElement fe)
            return;

        fe.SizeChanged -= OnSizeChanged;
        if (GetRadius(fe) > 0)
        {
            fe.SizeChanged += OnSizeChanged;
            Apply(fe);
        }
        else
        {
            fe.Clip = null;
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        var r = GetRadius(fe);
        if (r <= 0 || fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
            return;
        fe.Clip = new RectangleGeometry(
            new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
    }
}
