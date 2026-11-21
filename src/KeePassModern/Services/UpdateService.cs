using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeePassModern.Services;

/// <summary>
/// GitHub Releases 自动更新检查与流式下载服务（纯托管、System.Text.Json 源码生成，100% NativeAOT 兼容）。
/// </summary>
public sealed class UpdateService
{
    private const string GitHubReleasesApiUrl = "https://api.github.com/repos/pengpercy/KeePassModern/releases";
    private readonly HttpClient _httpClient;

    public UpdateService()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "KeePassModern");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public async Task<ReleaseInfo?> CheckForUpdatesAsync(string currentVersion)
    {
        var release = await GetLatestReleaseAsync();
        if (release is null) return null;

        var serverVersion = release.TagName.TrimStart('v', 'V');
        if (Version.TryParse(serverVersion, out var sVer) &&
            Version.TryParse(currentVersion.TrimStart('v', 'V'), out var cVer) &&
            sVer > cVer)
        {
            return release;
        }
        return null;
    }

    public async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        var response = await _httpClient.GetAsync(GitHubReleasesApiUrl);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        var json = await response.Content.ReadAsStringAsync();
        var releases = JsonSerializer.Deserialize(json, GitHubUpdateJsonContext.Default.ListReleaseInfo) ?? new List<ReleaseInfo>();

        return releases
            .Where(r => !r.Draft && !r.Prerelease && Version.TryParse(r.TagName.TrimStart('v', 'V'), out _))
            .OrderByDescending(r => Version.Parse(r.TagName.TrimStart('v', 'V')))
            .FirstOrDefault();
    }

    public async Task DownloadUpdateAsync(string downloadUrl, string destinationPath, IProgress<double> progress, CancellationToken ct = default)
    {
        using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[16384];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            if (totalBytes > 0)
                progress.Report((double)totalRead / totalBytes);
        }
    }
}

public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("assets")]
    public List<ReleaseAsset> Assets { get; set; } = new();
}

public sealed class ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReleaseInfo))]
[JsonSerializable(typeof(List<ReleaseInfo>))]
public partial class GitHubUpdateJsonContext : JsonSerializerContext;

/// <summary>
/// 按当前操作系统与 CPU 架构（x64 / arm64 / win7-x64）自动匹配最合适的 GitHub Release 安装包（与 Downio 命名规范完全一致）。
/// </summary>
public static class UpdateAssetSelector
{
    public static ReleaseAsset? SelectPreferredAsset(ReleaseInfo release)
    {
        var version = release.TagName.TrimStart('v', 'V');
        var arch = RuntimeInformation.ProcessArchitecture;

        if (OperatingSystem.IsWindows())
        {
            var runtime = arch == Architecture.Arm64 ? "win-arm64"
                : OperatingSystem.IsWindowsVersionAtLeast(10) ? "win-x64"
                : "win7-x64";
            var exact = $"KeePassModern_{version}.{runtime}.zip";
            return release.Assets.FirstOrDefault(a => a.Name.Equals(exact, StringComparison.OrdinalIgnoreCase))
                   ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith($".{runtime}.zip", StringComparison.OrdinalIgnoreCase))
                   ?? release.Assets.FirstOrDefault(a => a.Name.Contains(runtime, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        if (OperatingSystem.IsMacOS())
        {
            var runtime = arch == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            var zipExact = $"KeePassModern_{version}.{runtime}.zip";
            var dmgExact = $"KeePassModern_{version}.{runtime}.dmg";

            // 优先匹配 ditto 打包的 .zip（可直接在进程内解压更新，无需挂载 hdiutil）
            return release.Assets.FirstOrDefault(a => a.Name.Equals(zipExact, StringComparison.OrdinalIgnoreCase))
                   ?? release.Assets.FirstOrDefault(a => a.Name.Equals(dmgExact, StringComparison.OrdinalIgnoreCase))
                   ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith($".{runtime}.zip", StringComparison.OrdinalIgnoreCase))
                   ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith($".{runtime}.dmg", StringComparison.OrdinalIgnoreCase));
        }

        var linuxRuntime = arch == Architecture.Arm64 ? "aarch64" : "x86_64";
        var appImageExact = $"KeePassModern-{version}.linux.{linuxRuntime}.AppImage";
        return release.Assets.FirstOrDefault(a => a.Name.Equals(appImageExact, StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith($".linux.{linuxRuntime}.AppImage", StringComparison.OrdinalIgnoreCase))
               ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// 跨平台静默热替换升级器（参考 Downio：macOS .app 替换、Windows ZIP 覆盖、Linux AppImage 替换并自动重启）。
/// </summary>
public static class UpdateApplier
{
    public static Task<bool> TryApplyUpdateAsync(string downloadedFilePath)
    {
        if (OperatingSystem.IsWindows()) return TryApplyWindowsZipAsync(downloadedFilePath);
        if (OperatingSystem.IsMacOS()) return TryApplyMacBundleAsync(downloadedFilePath);
        return TryApplyLinuxAppImageAsync(downloadedFilePath);
    }

    private static Task<bool> TryApplyWindowsZipAsync(string zipPath)
    {
        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(false);
        var processPath = Environment.ProcessPath;
        var targetDir = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(targetDir)) return Task.FromResult(false);

        var updaterDir = Path.Combine(Path.GetTempPath(), "KeePassModern", "updater");
        Directory.CreateDirectory(updaterDir);
        var extractedDir = Path.Combine(updaterDir, "extracted");
        var scriptPath = Path.Combine(updaterDir, "apply_update.bat");

        var bat = $"""
            @echo off
            set PID={Environment.ProcessId}
            :wait
            tasklist /fi "PID eq %PID%" | find "%PID%" >nul
            if "%ERRORLEVEL%"=="0" (timeout /t 1 /nobreak >nul & goto wait)
            rmdir /s /q "{extractedDir}" 2>nul
            mkdir "{extractedDir}"
            powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '{zipPath}' -DestinationPath '{extractedDir}' -Force"
            robocopy "{extractedDir}" "{targetDir}" /E /NFL /NDL /NJH /NJS /NP
            start "" "{processPath}"
            """;
        File.WriteAllText(scriptPath, bat, Encoding.UTF8);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updaterDir
        });
        return Task.FromResult(true);
    }

    private static Task<bool> TryApplyMacBundleAsync(string archivePath)
    {
        var processPath = Environment.ProcessPath;
        var appBundlePath = FindAppBundlePath(processPath);
        if (string.IsNullOrWhiteSpace(appBundlePath)) return Task.FromResult(false);

        var updaterDir = Path.Combine(Path.GetTempPath(), "KeePassModern", "updater");
        Directory.CreateDirectory(updaterDir);
        var scriptPath = Path.Combine(updaterDir, "apply_update.sh");

        var sh = $$"""
            #!/usr/bin/env bash
            set -e
            PID={{Environment.ProcessId}}
            ARCHIVE='{{QuoteSh(archivePath)}}'
            TARGET_APP='{{QuoteSh(appBundlePath)}}'
            while kill -0 "$PID" 2>/dev/null; do sleep 1; done
            TMPDIR=$(mktemp -d)
            trap 'rm -rf "$TMPDIR"' EXIT
            if [[ "$ARCHIVE" == *.zip ]]; then
              ditto -x -k "$ARCHIVE" "$TMPDIR"
            elif [[ "$ARCHIVE" == *.dmg ]]; then
              MOUNT_POINT=$(hdiutil attach -nobrowse -readonly "$ARCHIVE" | awk -F'\t' '/\/Volumes\//{print $NF; exit}')
              cp -R "$MOUNT_POINT"/*.app "$TMPDIR"/ || true
              hdiutil detach "$MOUNT_POINT" || true
            else
              exit 1
            fi
            NEW_APP=$(find "$TMPDIR" -maxdepth 2 -name '*.app' -print -quit)
            if [[ -z "$NEW_APP" ]]; then exit 1; fi
            BACKUP_APP="$TARGET_APP.bak"
            rm -rf "$BACKUP_APP" || true
            mv "$TARGET_APP" "$BACKUP_APP"
            ditto "$NEW_APP" "$TARGET_APP"
            xattr -dr com.apple.quarantine "$TARGET_APP" >/dev/null 2>&1 || true
            open "$TARGET_APP"
            rm -rf "$BACKUP_APP"
            """;
        File.WriteAllText(scriptPath, sh, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("/bin/bash", $"\"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updaterDir
        });
        return Task.FromResult(true);
    }

    private static Task<bool> TryApplyLinuxAppImageAsync(string downloadedPath)
    {
        var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrWhiteSpace(appImagePath) || !downloadedPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(false);

        var updaterDir = Path.Combine(Path.GetTempPath(), "KeePassModern", "updater");
        Directory.CreateDirectory(updaterDir);
        var scriptPath = Path.Combine(updaterDir, "apply_update.sh");

        var sh = $"""
            #!/usr/bin/env bash
            set -e
            PID={Environment.ProcessId}
            while kill -0 "$PID" 2>/dev/null; do sleep 1; done
            chmod +x '{QuoteSh(downloadedPath)}'
            mv -f '{QuoteSh(downloadedPath)}' '{QuoteSh(appImagePath)}'
            '{QuoteSh(appImagePath)}' >/dev/null 2>&1 &
            """;
        File.WriteAllText(scriptPath, sh, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("/bin/bash", $"\"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = updaterDir
        });
        return Task.FromResult(true);
    }

    private static string? FindAppBundlePath(string? processPath)
    {
        var dir = string.IsNullOrWhiteSpace(processPath) ? null : Path.GetDirectoryName(processPath);
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(dir))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string QuoteSh(string s) => s.Replace("'", "'\"'\"'");
}
