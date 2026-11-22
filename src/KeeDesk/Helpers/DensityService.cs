using Avalonia;

namespace KeeDesk.Helpers;

public enum UiDensity
{
    Compact,
    Normal,
    Comfortable
}

/// <summary>
/// 运行时热切换 Fluent 2 界面密度令牌（紧凑 / 标准 / 宽松），纯托管直接写入资源字典，
/// 无需重启且彻底避免动态 XAML 加载导致的 NativeAOT 裁剪警告（IL2026）。
/// </summary>
public static class DensityService
{
    public static UiDensity Current { get; private set; } = UiDensity.Normal;

    public static void Apply(UiDensity density)
    {
        Current = density;
        var app = Application.Current;
        if (app is null) return;

        var r = app.Resources;
        switch (density)
        {
            case UiDensity.Compact:
                r["DensityRowHeight"] = 34d;
                r["DensityNavItemHeight"] = 28d;
                r["DensityControlHeight"] = 28d;
                r["DensityAvatarSize"] = 24d;
                r["DensityFontSizeTitle"] = 16d;
                r["DensityFontSizeBody"] = 12d;
                r["DensityFontSizeCaption"] = 11d;
                r["DensityCardPadding"] = new Thickness(10, 8);
                r["DensityItemMargin"] = new Thickness(4, 1);
                r["DensityContainerPadding"] = new Thickness(10);
                r["DensityItemSpacing"] = 4d;
                r["DensityCardRadius"] = new CornerRadius(6);
                break;

            case UiDensity.Comfortable:
                r["DensityRowHeight"] = 64d;
                r["DensityNavItemHeight"] = 40d;
                r["DensityControlHeight"] = 36d;
                r["DensityAvatarSize"] = 42d;
                r["DensityFontSizeTitle"] = 22d;
                r["DensityFontSizeBody"] = 14.5d;
                r["DensityFontSizeCaption"] = 12.5d;
                r["DensityCardPadding"] = new Thickness(20, 16);
                r["DensityItemMargin"] = new Thickness(10, 4);
                r["DensityContainerPadding"] = new Thickness(20);
                r["DensityItemSpacing"] = 12d;
                r["DensityCardRadius"] = new CornerRadius(8);
                break;

            default:
                r["DensityRowHeight"] = 52d;
                r["DensityNavItemHeight"] = 34d;
                r["DensityControlHeight"] = 32d;
                r["DensityAvatarSize"] = 36d;
                r["DensityFontSizeTitle"] = 20d;
                r["DensityFontSizeBody"] = 13.5d;
                r["DensityFontSizeCaption"] = 12d;
                r["DensityCardPadding"] = new Thickness(16, 12);
                r["DensityItemMargin"] = new Thickness(8, 2);
                r["DensityContainerPadding"] = new Thickness(16);
                r["DensityItemSpacing"] = 8d;
                r["DensityCardRadius"] = new CornerRadius(8);
                break;
        }
    }

    public static void Apply(string? densityName)
    {
        if (Enum.TryParse<UiDensity>(densityName, ignoreCase: true, out var d))
            Apply(d);
        else
            Apply(UiDensity.Normal);
    }
}
