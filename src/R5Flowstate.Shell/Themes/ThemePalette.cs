using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R5Flowstate.Contracts;

namespace R5Flowstate.Shell;

/// <summary>
/// S21 runs on the green palette in Theme.xaml; S30.1 gets the same palette turned blue. Every green-family
/// entry is hue-rotated into an application-level override, which outranks the merged theme, so the XAML's
/// DynamicResource references repaint live. Dark entries also gain saturation so the window reads as blue
/// rather than slate, and buttons take a fixed blue. The window icon follows the same way.
/// </summary>
static class ThemePalette
{
    const double BlueRotation = 60;
    const double GreenHueMin = 90;
    const double GreenHueMax = 180;
    const double DarkLightnessMax = 0.45;
    const double DarkSaturationScale = 1.6;
    const double AccentLightnessMax = 0.72;
    const double AccentLightnessScale = 0.92;
    static readonly Color s_buttonBlue = Color.FromRgb(0x27, 0x6A, 0xFC);
    const string IconKey = "AppIcon";
    const string S30IconUri = "pack://application:,,,/Assets/app_s30.ico";
    const string SetupBackgroundKey = "SetupBackground";
    const string S30BackgroundUri = "pack://application:,,,/Assets/season_s30.jpg";

    static GameBuild? s_applied;
    static readonly List<object> s_overridden = new();

    public static void Apply(GameBuild build)
    {
        var app = Application.Current;
        if (app is null || s_applied == build)
            return;

        foreach (var key in s_overridden)
            app.Resources.Remove(key);
        s_overridden.Clear();

        if (build == GameBuild.S30)
        {
            foreach (var dict in app.Resources.MergedDictionaries)
            {
                foreach (var key in dict.Keys)
                {
                    var blue = FixedBlue(key) ?? Rotate(dict[key]);
                    if (blue is null)
                        continue;
                    app.Resources[key] = blue;
                    s_overridden.Add(key);
                }
            }

            app.Resources[IconKey] = BitmapFrame.Create(new Uri(S30IconUri));
            s_overridden.Add(IconKey);
            app.Resources[SetupBackgroundKey] = BitmapFrame.Create(new Uri(S30BackgroundUri));
            s_overridden.Add(SetupBackgroundKey);
        }

        s_applied = build;
    }

    static object? FixedBlue(object key)
    {
        switch (key as string)
        {
            case "AccentColor":
                return s_buttonBlue;
            case "PrimaryFill":
                return FrozenBrush(s_buttonBlue);
            case "PrimaryText":
                return FrozenBrush(Colors.White);
            default:
                return null;
        }
    }

    static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    static object? Rotate(object? value)
    {
        switch (value)
        {
            case Color c:
                return TryRotate(c, out var rc) ? rc : null;
            case SolidColorBrush b:
            {
                if (!TryRotate(b.Color, out var bc))
                    return null;
                var nb = new SolidColorBrush(bc) { Opacity = b.Opacity };
                nb.Freeze();
                return nb;
            }
            case GradientBrush g:
            {
                var ng = g.CloneCurrentValue();
                var any = false;
                foreach (var stop in ng.GradientStops)
                {
                    if (TryRotate(stop.Color, out var sc))
                    {
                        stop.Color = sc;
                        any = true;
                    }
                }
                if (!any)
                    return null;
                ng.Freeze();
                return ng;
            }
            default:
                return null;
        }
    }

    static bool TryRotate(Color c, out Color rotated)
    {
        rotated = c;
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta <= 0)
            return false;

        double h;
        if (max == r)
            h = 60 * (((g - b) / delta) % 6);
        else if (max == g)
            h = 60 * (((b - r) / delta) + 2);
        else
            h = 60 * (((r - g) / delta) + 4);
        if (h < 0)
            h += 360;
        if (h < GreenHueMin || h > GreenHueMax)
            return false;

        var l = (max + min) / 2;
        var s = delta / (1 - Math.Abs(2 * l - 1));
        if (l < DarkLightnessMax)
            s = Math.Min(1, s * DarkSaturationScale);
        else if (l < AccentLightnessMax)
            l *= AccentLightnessScale;
        rotated = FromHsl((h + BlueRotation) % 360, s, l, c.A);
        return true;
    }

    static Color FromHsl(double h, double s, double l, byte a)
    {
        var chroma = (1 - Math.Abs(2 * l - 1)) * s;
        var x = chroma * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = l - chroma / 2;
        var (r, g, b) = h switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        static byte To(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Color.FromArgb(a, To(r + m), To(g + m), To(b + m));
    }
}
