using Avalonia;
using Avalonia.Media;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Helpers;

public sealed record StandardIconInfo(int IconId, string NameKey, string ResourceKey)
{
    public string Name => L(NameKey);
}

/// <summary>
/// 将 KeePass 标准 IconId 映射为 Fluent 2 矢量图标（零 Emoji），供图标选择器与条目/分组展示复用。
/// </summary>
public static class KeePassIconCatalog
{
    public static IReadOnlyList<StandardIconInfo> StandardIcons { get; } =
    [
        new(0, "Str_KeyDefault", "IconKey"),
        new(1, "Str_WebGlobe", "IconGlobe"),
        new(2, "Str_SecurityShield", "IconShield"),
        new(3, "Str_ServerTerminal", "IconDatabase"),
        new(19, "Str_EmailLink", "IconLink"),
        new(23, "Str_EncryptedVault", "IconVault"),
        new(37, "Str_FinanceFavoriteStar", "IconStar"),
        new(43, "Str_RecycleBin", "IconTrash"),
        new(48, "Str_Folder", "IconFolder"),
        new(52, "Str_VerificationCodeClock", "IconClock"),
        new(56, "Str_LockedCredentials", "IconLock"),
        new(59, "Str_Tags", "IconTag"),
        new(61, "Str_DocumentAttachment", "IconAttach"),
        new(62, "Str_AutoTypeKeyboard", "IconKeyboard"),
        new(64, "Str_SystemSettings", "IconSettings"),
        new(68, "Str_History", "IconHistory")
    ];

    public static string GetResourceKey(int iconId)
        => StandardIcons.FirstOrDefault(i => i.IconId == iconId)?.ResourceKey ?? "IconKey";

    public static Geometry? GetGeometry(int iconId)
    {
        var key = GetResourceKey(iconId);
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var res) == true &&
            res is Geometry g)
            return g;
        return null;
    }
}
