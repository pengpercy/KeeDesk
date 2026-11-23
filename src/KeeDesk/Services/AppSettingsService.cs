using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeeDesk.Services;

public sealed class AppSettings
{
    public string ThemeMode { get; set; } = "Default"; // Default / Light / Dark
    public string AccentMode { get; set; } = "Emerald"; // System / Emerald / ClassicBlue / Indigo / Amber / Rose / Slate / Custom
    public string CustomAccentHex { get; set; } = "#508252";
    public string UiDensity { get; set; } = "Normal"; // Compact / Normal / Comfortable
    public string Language { get; set; } = "zh-CN";
    public int ClipboardClearSeconds { get; set; } = 15;
    public bool AutoLockIdleEnabled { get; set; } = true;
    public int AutoLockIdleMinutes { get; set; } = 10;
    public bool MinimizeToTray { get; set; } = true;
    public DesktopShortcut? AutoTypeShortcut { get; set; }
    public double? MainWindowWidth { get; set; }
    public double? MainWindowHeight { get; set; }
    public List<RecentVaultItem> RecentVaults { get; set; } = new();
}

public sealed class RecentVaultItem
{
    public string FileName => Path.GetFileName(FilePath);
    public string FilePath { get; set; } = string.Empty;
    public string? KeyFilePath { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public DateTimeOffset LastOpened { get; set; } = DateTimeOffset.UtcNow;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext;

public sealed class AppSettingsService
{
    private readonly string _settingsPath;
    public AppSettings Settings { get; private set; }
    internal string RecoveryDirectory => Path.Combine(Path.GetDirectoryName(_settingsPath)!, "Recovery");

    public AppSettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appDir = Path.Combine(appData, "KeeDesk");
        Directory.CreateDirectory(appDir);
        _settingsPath = Path.Combine(appDir, "settings.json");
        Settings = Load(_settingsPath)
            ?? Load(Path.Combine(appData, "KeePassModern", "settings.json"))
            ?? new AppSettings();
    }

    private static AppSettings? Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            }
        }
        catch { }
        return null;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Settings, AppSettingsJsonContext.Default.AppSettings);
            File.WriteAllText(_settingsPath, json);
        }
        catch { }
    }

    public void AddRecentVault(string filePath, string? keyFilePath, string displayName)
    {
        Settings.RecentVaults.RemoveAll(r => string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        Settings.RecentVaults.Insert(0, new RecentVaultItem
        {
            FilePath = filePath,
            KeyFilePath = keyFilePath,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(filePath) : displayName,
            LastOpened = DateTimeOffset.UtcNow
        });
        if (Settings.RecentVaults.Count > 10)
            Settings.RecentVaults.RemoveRange(10, Settings.RecentVaults.Count - 10);
        Save();
    }

    public void RemoveRecentVault(string filePath)
    {
        Settings.RecentVaults.RemoveAll(r => string.Equals(r.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    public void ClearRecentVaults()
    {
        Settings.RecentVaults.Clear();
        Save();
    }
}
