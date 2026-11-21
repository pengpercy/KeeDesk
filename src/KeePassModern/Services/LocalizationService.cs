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
        ["Str_AllEntries"] = "全部条目",
        ["Str_Favorites"] = "收藏夹",
        ["Str_Totp2Fa"] = "动态验证码 (2FA)",
        ["Str_SecurityAudit"] = "安全审计",
        ["Str_Groups"] = "密码分组",
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
        ["Str_TotpLabel"] = "动态验证码（TOTP）",
        ["Str_Url"] = "网址",
        ["Str_NotesAndAttachments"] = "备注与附件",
        ["Str_EmptyDetailHint"] = "在左侧选择一个密码条目查看详情，或点击「新建条目」添加",
        ["Str_EmptyRecycleBin"] = "清空回收站"
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
        ["Str_EmptyRecycleBin"] = "Empty Recycle Bin"
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
