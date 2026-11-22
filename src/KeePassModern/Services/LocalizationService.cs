using System.Globalization;
using Avalonia;

namespace KeePassModern.Services;

/// <summary>
/// 纯托管、零反射、NativeAOT 友好的中英文实时多语言切换服务。
/// 通过运行时更新 Application.Current.Resources 中的 Str_* 动态词条，无需重启即可生效。
/// </summary>
public static class LocalizationService
{
    public static string CurrentLanguage { get; private set; } = "zh-CN";

    private static readonly Dictionary<string, string> ZhCn = new(StringComparer.Ordinal)
    {
        ["Str_SmartViews"] = "智能视图",
        ["Str_AllEntries"] = "所有条目",
        ["Str_Favorites"] = "收藏",
        ["Str_Totp2Fa"] = "动态验证码",
        ["Str_SecurityAudit"] = "安全审计",
        ["Str_Groups"] = "分组",
        ["Str_TagFilter"] = "标签筛选",
        ["Str_LockVault"] = "锁定密码库",
        ["Str_SearchPlaceholder"] = "搜索密码库",
        ["Str_RecentVaults"] = "最近打开的密码库",
        ["Str_NewVaultBtn"] = "+ 新建密码库...",
        ["Str_OpenLocalBtn"] = "打开本地 .kdbx 文件...",
        ["Str_TryDemoBtn"] = "立即体验演示密码库 →",
        ["Str_MasterPassword"] = "主密码",
        ["Str_MasterPasswordPlaceholder"] = "请输入主密码...",
        ["Str_KeyFileOptional"] = "密钥文件（可选）",
        ["Str_UnlockVaultBtn"] = "解锁密码库",
        ["Str_UnlockingBtn"] = "正在解密密码库...",
        ["Str_DragDropHint"] = "支持直接拖拽 .kdbx 文件到此窗口打开",
        ["Str_UserName"] = "用户名",
        ["Str_Password"] = "密码",
        ["Str_TotpLabel"] = "动态验证码",
        ["Str_Url"] = "网站",
        ["Str_NotesAndAttachments"] = "备注与附件",
        ["Str_EmptyDetailHint"] = "在左侧选择一个密码条目查看详情，或点击「新建条目」添加",
        ["Str_EmptyRecycleBin"] = "清空回收站",
        ["Str_SettingsTitle"] = "设置",
        ["Str_NavAppearance"] = "外观与密度",
        ["Str_NavSecurity"] = "安全与锁定",
        ["Str_NavAutoType"] = "自动输入热键",
        ["Str_NavData"] = "同步与导入导出",
        ["Str_NavAbout"] = "关于与更新",
        ["Str_AppearanceHeading"] = "外观与界面密度",
        ["Str_ThemeMode"] = "外观模式",
        ["Str_ThemeSystem"] = "跟随系统",
        ["Str_ThemeLight"] = "浅色模式",
        ["Str_ThemeDark"] = "深色模式",
        ["Str_BatchMove"] = "移动",
        ["Str_BatchFav"] = "收藏",
        ["Str_BatchDelete"] = "删除"
    };

    private static readonly Dictionary<string, string> EnUs = new(StringComparer.Ordinal)
    {
        ["Str_SmartViews"] = "Smart Views",
        ["Str_AllEntries"] = "All Entries",
        ["Str_Favorites"] = "Favorites",
        ["Str_Totp2Fa"] = "Authenticator (2FA)",
        ["Str_SecurityAudit"] = "Security Audit",
        ["Str_Groups"] = "Groups",
        ["Str_TagFilter"] = "Tags",
        ["Str_LockVault"] = "Lock Vault",
        ["Str_SearchPlaceholder"] = "Search vault",
        ["Str_RecentVaults"] = "Recent Vaults",
        ["Str_NewVaultBtn"] = "+ Create New Vault...",
        ["Str_OpenLocalBtn"] = "Open Local .kdbx File...",
        ["Str_TryDemoBtn"] = "Try Demo Vault →",
        ["Str_MasterPassword"] = "Master Password",
        ["Str_MasterPasswordPlaceholder"] = "Enter master password...",
        ["Str_KeyFileOptional"] = "Key File (Optional)",
        ["Str_UnlockVaultBtn"] = "Unlock Vault",
        ["Str_UnlockingBtn"] = "Decrypting vault...",
        ["Str_DragDropHint"] = "Drag and drop a .kdbx file anywhere on this window to open",
        ["Str_UserName"] = "Username",
        ["Str_Password"] = "Password",
        ["Str_TotpLabel"] = "One-Time Password (TOTP)",
        ["Str_Url"] = "URL",
        ["Str_NotesAndAttachments"] = "Notes & Attachments",
        ["Str_EmptyDetailHint"] = "Select an entry on the left to inspect, or click '+' to add one",
        ["Str_EmptyRecycleBin"] = "Empty Recycle Bin",
        ["Str_SettingsTitle"] = "Settings",
        ["Str_NavAppearance"] = "Appearance & Density",
        ["Str_NavSecurity"] = "Security & Lock",
        ["Str_NavAutoType"] = "Auto-Type Hotkey",
        ["Str_NavData"] = "Sync & Import/Export",
        ["Str_NavAbout"] = "About & Updates",
        ["Str_AppearanceHeading"] = "Appearance & UI Density",
        ["Str_ThemeMode"] = "Theme Mode",
        ["Str_ThemeSystem"] = "Follow System",
        ["Str_ThemeLight"] = "Light",
        ["Str_ThemeDark"] = "Dark",
        ["Str_BatchMove"] = "Move",
        ["Str_BatchFav"] = "Star",
        ["Str_BatchDelete"] = "Delete"
    };

    public static void Apply(string? languageCode)
    {
        bool isEn = string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase);
        CurrentLanguage = isEn ? "en-US" : "zh-CN";

        var culture = new CultureInfo(CurrentLanguage);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var app = Application.Current;
        if (app is null) return;

        var map = isEn ? EnUs : ZhCn;
        foreach (var kv in map)
            app.Resources[kv.Key] = kv.Value;
    }
}
