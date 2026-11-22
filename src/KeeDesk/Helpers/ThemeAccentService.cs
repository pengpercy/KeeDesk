using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace KeeDesk.Helpers;

public sealed record AccentPreset(string Id, string Name, string LightHex, string DarkHex);

public static class ThemeAccentService
{
    public const string DefaultAccent = "#508252";

    public static readonly IReadOnlyList<AccentPreset> Presets =
    [
        new("System", "跟随系统", DefaultAccent, DefaultAccent),
        new("Emerald", "橄榄绿", DefaultAccent, DefaultAccent),
        new("ClassicBlue", "经典蓝", "#2563EB", "#3B82F6"),
        new("Indigo", "极光靛", "#4F46E5", "#6366F1"),
        new("Amber", "琥珀金", "#B45309", "#D97706"),
        new("Rose", "玫瑰红", "#BE123C", "#E11D48"),
        new("Slate", "石墨灰", "#475569", "#64748B")
    ];

    public static void ApplyThemeVariant(string themeMode)
    {
        var app = Application.Current;
        if (app is null) return;
        app.RequestedThemeVariant = themeMode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }

    public static void ApplyAccent(string accentMode, string? customAccentHex)
    {
        var app = Application.Current;
        if (app is null) return;

        if (WindowsContrastPalette.TryApply(app)) return;

        var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
        Color accent;

        if (string.Equals(accentMode, "System", StringComparison.OrdinalIgnoreCase))
        {
            ClearSystemOverrides(app);
            var platformAccent = app.PlatformSettings?.GetColorValues().AccentColor1;
            accent = platformAccent is { A: > 0 } pa
                ? (isDark ? EnsureDarkThemeVivid(pa) : pa)
                : Color.Parse(DefaultAccent);
            ApplyActionBrushes(app, accent, isDark);
            return;
        }

        string? hex = customAccentHex;
        if (!string.Equals(accentMode, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            var preset = Presets.FirstOrDefault(p => string.Equals(p.Id, accentMode, StringComparison.OrdinalIgnoreCase));
            hex = preset is not null ? (isDark ? preset.DarkHex : preset.LightHex) : DefaultAccent;
        }

        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out accent))
            accent = Color.Parse(DefaultAccent);

        var r = app.Resources;
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorDark1"] = Darken(accent, 0.14);
        r["SystemAccentColorDark2"] = Darken(accent, 0.26);
        r["SystemAccentColorDark3"] = Darken(accent, 0.40);
        r["SystemAccentColorLight1"] = Lighten(accent, 0.14);
        r["SystemAccentColorLight2"] = Lighten(accent, 0.28);
        r["SystemAccentColorLight3"] = Lighten(accent, 0.42);

        var brush = new SolidColorBrush(accent);
        r["SystemControlHighlightAccentBrush"] = brush;
        r["SystemControlForegroundAccentBrush"] = new SolidColorBrush(GetAccentText(accent, isDark));
        r["SystemControlBackgroundAccentBrush"] = brush;
        r["SystemAccentColorBrush"] = brush;

        ApplyActionBrushes(app, accent, isDark);
    }

    private static void ApplyActionBrushes(Application app, Color accent, bool isDark)
    {
        var r = app.Resources;
        bool isV3Green = accent == Color.Parse(DefaultAccent);
        var hover = isV3Green ? Color.Parse("#48754A") : isDark ? Lighten(accent, .1) : Darken(accent, .12);
        var pressed = isV3Green ? Color.Parse("#406841") : Darken(accent, isDark ? .12 : .22);
        r["AccentButtonBackgroundBrush"] = new SolidColorBrush(accent);
        r["AccentButtonHoverBackgroundBrush"] = new SolidColorBrush(hover);
        r["AccentButtonPressedBackgroundBrush"] = new SolidColorBrush(pressed);
        r["AccentActionForegroundBrush"] = new SolidColorBrush(GetContrastForeground(accent));
        r["AccentActionHoverForegroundBrush"] = new SolidColorBrush(GetContrastForeground(hover));
        r["AccentActionPressedForegroundBrush"] = new SolidColorBrush(GetContrastForeground(pressed));
        r["AccentTextBrush"] = new SolidColorBrush(GetAccentText(accent, isDark));

        r["TextControlFocusBottomShadow"] = new BoxShadows(new BoxShadow
        {
            IsInset = true,
            OffsetX = 0,
            OffsetY = -2.5,
            Blur = 0,
            Spread = 0,
            Color = accent
        });
    }

    private static void ClearSystemOverrides(Application app)
    {
        var r = app.Resources;
        r.Remove("SystemAccentColor");
        r.Remove("SystemAccentColorDark1");
        r.Remove("SystemAccentColorDark2");
        r.Remove("SystemAccentColorDark3");
        r.Remove("SystemAccentColorLight1");
        r.Remove("SystemAccentColorLight2");
        r.Remove("SystemAccentColorLight3");
        r.Remove("SystemControlHighlightAccentBrush");
        r.Remove("SystemControlForegroundAccentBrush");
        r.Remove("SystemControlBackgroundAccentBrush");
        r.Remove("SystemAccentColorBrush");
    }

    private static Color EnsureDarkThemeVivid(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return lum < 0.45 ? Lighten(c, 0.30) : c;
    }

    private static Color GetContrastForeground(Color background)
    {
        double value = RelativeLuminance(background);
        return 1.05 / (value + .05) >= 4.5 ? Colors.White : Colors.Black;
    }

    private static Color GetAccentText(Color accent, bool isDark)
    {
        if (accent == Color.Parse(DefaultAccent))
            return Color.Parse(isDark ? "#B2CEB2" : "#345E36");
        double surface = RelativeLuminance(Color.Parse(isDark ? "#2C2C2C" : "#FFFFFF"));
        var color = accent;
        for (int step = 1; step <= 10 && Contrast(RelativeLuminance(color), surface) < 4.5; step++)
            color = isDark ? Lighten(accent, step / 10.0) : Darken(accent, step / 10.0);
        return color;
    }

    private static double Contrast(double a, double b) => (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte c)
        {
            double v = c / 255.0;
            return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
        }
        return .2126 * Linearize(color.R) + .7152 * Linearize(color.G) + .0722 * Linearize(color.B);
    }

    private static Color Lighten(Color c, double amount)
        => Color.FromArgb(c.A,
            (byte)Math.Clamp(c.R + (255 - c.R) * amount, 0, 255),
            (byte)Math.Clamp(c.G + (255 - c.G) * amount, 0, 255),
            (byte)Math.Clamp(c.B + (255 - c.B) * amount, 0, 255));

    private static Color Darken(Color c, double amount)
        => Color.FromArgb(c.A,
            (byte)Math.Clamp(c.R * (1 - amount), 0, 255),
            (byte)Math.Clamp(c.G * (1 - amount), 0, 255),
            (byte)Math.Clamp(c.B * (1 - amount), 0, 255));
}
