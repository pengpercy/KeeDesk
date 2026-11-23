using System.Text.Json;

namespace KeeDesk.Core.Services;

public enum CloudVaultProvider { GoogleDrive, OneDrive, Dropbox, ICloudDrive, Other }
public sealed record CloudVaultLocation(CloudVaultProvider Provider, string DirectoryPath);

public static class CloudVaultLocations
{
    public static IReadOnlyList<CloudVaultLocation> Discover()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Discover(home, appData, localData, OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(),
            Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"),
            Environment.GetEnvironmentVariable("OneDriveCommercial"));
    }

    internal static IReadOnlyList<CloudVaultLocation> Discover(string home, string appData, string localData,
        bool macOS, bool windows, params string?[] oneDrivePaths)
    {
        var candidates = new List<CloudVaultLocation>();
        var seen = new HashSet<(CloudVaultProvider, string)>();

        void Add(CloudVaultProvider provider, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return;
            var directory = new DirectoryInfo(path);
            string full = directory.FullName;
            try { full = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            var comparisonPath = windows ? full.ToUpperInvariant() : full;
            if (seen.Add((provider, comparisonPath)))
                candidates.Add(new CloudVaultLocation(provider, full));
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            if (macOS)
            {
                var fileProvider = Path.Combine(home, "Library", "CloudStorage");
                foreach (var directory in EnumerateDirectories(fileProvider))
                {
                    var name = Path.GetFileName(directory);
                    if (ProviderForFolderName(name) is { } provider)
                        Add(provider, directory);
                }
                Add(CloudVaultProvider.GoogleDrive, "/Volumes/GoogleDrive");
                Add(CloudVaultProvider.ICloudDrive, Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs"));
            }
            foreach (var name in new[] { "Google Drive", "OneDrive", "Dropbox", "iCloudDrive" })
                if (ProviderForFolderName(name) is { } provider) Add(provider, Path.Combine(home, name));
            ReadDropboxConfig(Path.Combine(home, ".dropbox", "info.json"), Add);
        }

        foreach (var path in oneDrivePaths) Add(CloudVaultProvider.OneDrive, path);

        if (windows)
        {
            foreach (var directory in new[] { appData, localData }.Where(directory => !string.IsNullOrWhiteSpace(directory)))
                ReadDropboxConfig(Path.Combine(directory, "Dropbox", "info.json"), Add);
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady && ProviderForFolderName(drive.VolumeLabel) == CloudVaultProvider.GoogleDrive)
                        Add(CloudVaultProvider.GoogleDrive, drive.RootDirectory.FullName);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        return candidates;
    }

    internal static CloudVaultProvider? ProviderForFolderName(string name)
    {
        if (name.StartsWith("GoogleDrive", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Google Drive", StringComparison.OrdinalIgnoreCase)) return CloudVaultProvider.GoogleDrive;
        if (name.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) return CloudVaultProvider.OneDrive;
        if (name.StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase)) return CloudVaultProvider.Dropbox;
        if (name.StartsWith("iCloudDrive", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("iCloud Drive", StringComparison.OrdinalIgnoreCase)) return CloudVaultProvider.ICloudDrive;
        return null;
    }

    private static string[] EnumerateDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static void ReadDropboxConfig(string path, Action<CloudVaultProvider, string?> add)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var file = File.OpenRead(path);
            using var json = JsonDocument.Parse(file);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var account in json.RootElement.EnumerateObject())
                if (account.Value.ValueKind == JsonValueKind.Object && account.Value.TryGetProperty("path", out var folder) &&
                    folder.ValueKind == JsonValueKind.String)
                    add(CloudVaultProvider.Dropbox, folder.GetString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }
}
