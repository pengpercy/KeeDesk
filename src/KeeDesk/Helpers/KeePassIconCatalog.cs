using Avalonia;
using Avalonia.Media;

namespace KeeDesk.Helpers;

public sealed record StandardIconInfo(int IconId, string Name, string ResourceKey);

/// <summary>
/// 将 KeePass 标准 IconId 映射为 Fluent 2 矢量图标（零 Emoji），供图标选择器与条目/分组展示复用。
/// </summary>
public static class KeePassIconCatalog
{
    public static IReadOnlyList<StandardIconInfo> StandardIcons { get; } =
    [
        new(0, "钥匙（默认）", "IconKey"),
        new(1, "网页 / 地球", "IconGlobe"),
        new(2, "安全盾牌", "IconShield"),
        new(3, "服务器 / 终端", "IconDatabase"),
        new(19, "电子邮箱 / 链接", "IconLink"),
        new(23, "加密保险库", "IconVault"),
        new(37, "金融 / 收藏星标", "IconStar"),
        new(43, "回收站", "IconTrash"),
        new(48, "文件夹", "IconFolder"),
        new(52, "动态验证码 / 时钟", "IconClock"),
        new(56, "锁定凭证", "IconLock"),
        new(59, "标签", "IconTag"),
        new(61, "附件文档", "IconAttach"),
        new(62, "自动输入键盘", "IconKeyboard"),
        new(64, "系统配置", "IconSettings"),
        new(68, "历史记录", "IconHistory")
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
