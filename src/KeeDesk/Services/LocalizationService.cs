using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Styling;
using KeeDesk.Core.Services;

namespace KeeDesk.Services;

/// <summary>
/// 纯托管、零反射、NativeAOT 友好的中英文实时多语言切换服务。
/// 在语言切换时一次更新所有 Str_* 动态词条。
/// </summary>
public static partial class LocalizationService
{
    public static string CurrentLanguage { get; private set; } = "zh-CN";
    internal static event Action? LanguageChanged;
    public static IMultiValueConverter FormatConverter { get; } = new LocalizedFormatConverter();

    private sealed class LocalizedFormatConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count != 2 || values[0] is not string format || ReferenceEquals(values[1], AvaloniaProperty.UnsetValue))
                return AvaloniaProperty.UnsetValue;

            return string.Format(CultureInfo.CurrentCulture, format, values[1]);
        }
    }

    private sealed class LocalizedResources(Dictionary<string, string> initialStrings) : ResourceProvider
    {
        private Dictionary<string, string> _strings = initialStrings;

        public override bool HasResources => true;

        public override bool TryGetResource(object key, ThemeVariant? theme, out object? value)
        {
            value = key is string name && _strings.TryGetValue(name, out var text) ? text : null;
            return value is not null;
        }

        public void Update(Dictionary<string, string> strings)
        {
            if (ReferenceEquals(_strings, strings)) return;
            _strings = strings;
            RaiseResourcesChanged();
        }
    }

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
        ["Str_GlobalAutoType"] = "全局自动输入",
        ["Str_GlobalAutoTypeHint"] = "解锁密码库后，在目标窗口按下热键以匹配条目。",
        ["Str_GlobalAutoTypeUnavailable"] = "当前桌面会话无法注册全局热键。可右键单击条目，选择「执行自动输入」。",
        ["Str_AppShortcuts"] = "应用内快捷键",
        ["Str_SearchEntriesShortcut"] = "搜索条目",
        ["Str_NewEntryShortcut"] = "新建条目",
        ["Str_SaveVaultShortcut"] = "保存当前密码库",
        ["Str_AccentOliveGreen"] = "橄榄绿",
        ["Str_AboutTagline"] = "本地密码管理器",
        ["Str_AboutDescription"] = "打开已有的 KeePass 密码库，集中管理密码和动态验证码，自动输入登录信息。",
        ["Str_CheckForUpdates"] = "检查更新",
        ["Str_CheckingForUpdates"] = "正在检查更新…",
        ["Str_OpenGitHubRepository"] = "访问 GitHub 仓库",
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
        ["Str_GlobalAutoType"] = "Global Auto-Type",
        ["Str_GlobalAutoTypeHint"] = "Unlock a vault, then press the hotkey in the target window to match an entry.",
        ["Str_GlobalAutoTypeUnavailable"] = "This desktop session cannot register a global shortcut. Right-click an entry and select Perform Auto-Type.",
        ["Str_AppShortcuts"] = "In-app shortcuts",
        ["Str_SearchEntriesShortcut"] = "Search entries",
        ["Str_NewEntryShortcut"] = "New entry",
        ["Str_SaveVaultShortcut"] = "Save current vault",
        ["Str_AccentOliveGreen"] = "Olive green",
        ["Str_AboutTagline"] = "Local password manager",
        ["Str_AboutDescription"] = "Open your existing KeePass vaults, manage passwords and verification codes, and enter sign-in details with Auto-Type.",
        ["Str_CheckForUpdates"] = "Check for updates",
        ["Str_CheckingForUpdates"] = "Checking for updates…",
        ["Str_OpenGitHubRepository"] = "Visit GitHub repository",
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

    static LocalizationService()
    {
        foreach (var (key, chinese, english) in UiStrings)
        {
            ZhCn.Add(key, chinese);
            EnUs.Add(key, english);
        }
    }

    public static string L(string key) => (CurrentLanguage == "en-US" ? EnUs : ZhCn)[key];

    public static string L(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, L(key), args);

    public static string FormatPasswordStrength(PasswordStrengthResult result, bool isEmpty = false)
    {
        var key = isEmpty ? "Str_EmptyPassword2" : result.Level switch
        {
            PasswordStrengthLevel.VeryWeak => "Str_VeryWeak",
            PasswordStrengthLevel.Weak => "Str_Weak",
            PasswordStrengthLevel.Fair => "Str_Fair",
            PasswordStrengthLevel.Strong => "Str_Strong",
            _ => "Str_VeryStrong"
        };
        return L("Str_BitsOfEntropyFormat", L(key), result.EntropyBits);
    }

    public static void Apply(string? languageCode)
    {
        bool isEn = string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase);
        var language = isEn ? "en-US" : "zh-CN";
        bool changed = CurrentLanguage != language;
        CurrentLanguage = language;

        var culture = new CultureInfo(CurrentLanguage);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var app = Application.Current;
        if (app is not null)
        {
            var map = isEn ? EnUs : ZhCn;
            var dictionaries = app.Resources.MergedDictionaries;
            if (dictionaries.OfType<LocalizedResources>().FirstOrDefault() is { } localized)
                localized.Update(map);
            else
                dictionaries.Add(new LocalizedResources(map));
        }

        if (changed) LanguageChanged?.Invoke();
    }
}
