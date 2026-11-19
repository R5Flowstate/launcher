using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace R5Flowstate.Shell.Linux.Views;

/// <summary>
/// Theme values for the parts of the UI that are built in code rather than in
/// XAML. Avalonia has no <c>SetResourceReference</c>, so a code-built view has
/// to look the resource up itself — and has to keep working when it is built
/// before it is attached to the tree, which is why the search goes view, then
/// application, then the literal.
///
/// The literals are the palette values from R5FTheme.axaml. They exist so a
/// code-built view can never render invisible text; the palette itself still
/// lives in one place, the theme.
/// </summary>
static class ThemeLookup
{
    public static IBrush Brush(Visual? origin, string key, string fallback)
    {
        if (Find(origin, key) is IBrush hit)
            return hit;
        return Avalonia.Media.Brush.Parse(fallback);
    }

    public static FontFamily Font(Visual? origin, string key, FontFamily fallback)
    {
        if (Find(origin, key) is FontFamily hit)
            return hit;
        return fallback;
    }

    static object? Find(Visual? origin, string key)
    {
        if (origin is not null
            && ResourceNodeExtensions.TryFindResource(origin, key, out var local)
            && local is not null)
        {
            return local;
        }

        if (Application.Current is { } app
            && ResourceNodeExtensions.TryFindResource(app, key, out var appLevel)
            && appLevel is not null)
        {
            return appLevel;
        }

        return null;
    }
}
