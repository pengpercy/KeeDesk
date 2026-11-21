using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace KeePassModern.Helpers;

public sealed record AccentPreset(string Id, string Name, string LightHex, string DarkHex);

/// <summary>
/// 主题与强调色服务：默认跟随操作系统强调色，同时提供 6 款内置预设与自定义取色。
/// 严格遵循画板 02/03/11 的 Fluent 2 明暗对比度规范（浅色深底白字，深色亮底深字）。
/// </summary>
public static class ThemeAccentService
{
    public static readonly IReadOnlyList<AccentPreset> Presets =
    [
        new("System", "跟随系统", "#056D50", "#10B981"),
        new("Emerald", "青翠安全绿", "#056D50", "#10B981"),
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

        var isDark = app.ActualThemeVariant == ThemeVariant.Dark;
        Color accent;

        if (string.Equals(accentMode, "System", StringComparison.OrdinalIgnoreCase))
        {
            ClearSystemOverrides(app);
            var platformAccent = app.PlatformSettings?.GetColorValues().AccentColor1;
            accent = platformAccent is { A: > 0 } pa
                ? (isDark ? EnsureDarkThemeVivid(pa) : pa)
                : Color.Parse(isDark ? "#6EE7B7" : "#056D50");
            ApplyActionBrushes(app, accent, isDark);
            return;
        }

        string? hex = customAccentHex;
        if (!string.Equals(accentMode, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            var preset = Presets.FirstOrDefault(p => string.Equals(p.Id, accentMode, StringComparison.OrdinalIgnoreCase));
            hex = preset is not null ? (isDark ? preset.DarkHex : preset.LightHex) : (isDark ? "#6EE7B7" : "#056D50");
        }

        if (string.IsNullOrWhiteSpace(hex) || !Color.TryParse(hex, out accent))
            accent = Color.Parse(isDark ? "#6EE7B7" : "#056D50");

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
        r["SystemControlForegroundAccentBrush"] = brush;
        r["SystemControlBackgroundAccentBrush"] = brush;
        r["SystemAccentColorBrush"] = brush;

        ApplyActionBrushes(app, accent, isDark);
    }

    private static void ApplyActionBrushes(Application app, Color accent, bool isDark)
    {
        var r = app.Resources;
        r["AccentButtonBackgroundBrush"] = new SolidColorBrush(accent);
        r["AccentButtonHoverBackgroundBrush"] = new SolidColorBrush(isDark ? Lighten(accent, 0.10) : Darken(accent, 0.12));
        r["AccentButtonPressedBackgroundBrush"] = new SolidColorBrush(isDark ? Darken(accent, 0.12) : Darken(accent, 0.22));
        r["AccentActionForegroundBrush"] = new SolidColorBrush(GetContrastForeground(accent));

        // Fluent 2 输入框获焦底部强调色下划线（配合 BorderThickness="1,1,1,0" 紧贴最底缘，不被灰边遮挡）
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

    private static Color GetContrastForeground(Color bg)
    {
        double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return luminance > 0.58 ? Color.Parse("#052E16") : Colors.White;
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
