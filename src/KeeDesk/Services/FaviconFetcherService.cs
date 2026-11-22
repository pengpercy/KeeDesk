using System.Security.Cryptography;
using Avalonia.Media.Imaging;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Services;

/// <summary>
/// 官网 Favicon 自动抓取服务：根据条目网址或邮箱域名自动获取高清网站图标，
/// 并支持按 SHA-256 去重写入 KDBX Meta.CustomIcons（100% NativeAOT 友好）。
/// </summary>
public static class FaviconFetcherService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    static FaviconFetcherService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 KeeDesk/1.0");
    }

    /// <summary>
    /// 优先从网址提取域名；若网址为空，则从邮箱地址（user@domain.com）提取域名。
    /// </summary>
    public static string? ExtractDomain(string? url, string? userNameOrEmail)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            var raw = url.Trim();
            if (!raw.Contains("://", StringComparison.Ordinal))
                raw = "https://" + raw;
            if (Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
                !string.IsNullOrWhiteSpace(uri.Host) &&
                uri.Host.Contains('.'))
            {
                return uri.Host.ToLowerInvariant();
            }
        }

        if (!string.IsNullOrWhiteSpace(userNameOrEmail))
        {
            var at = userNameOrEmail.LastIndexOf('@');
            if (at > 0 && at < userNameOrEmail.Length - 1)
            {
                var domain = userNameOrEmail[(at + 1)..].Trim().ToLowerInvariant();
                if (domain.Contains('.') && !domain.Contains(' '))
                    return domain;
            }
        }

        return null;
    }

    /// <summary>
    /// 根据域名自动尝试多个高清 Favicon 源，返回可被 Avalonia 解码的图片字节。
    /// </summary>
    public static async Task<byte[]?> FetchByDomainAsync(string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;
        domain = domain.Trim().ToLowerInvariant();

        string[] candidates =
        [
            $"https://www.google.com/s2/favicons?domain={Uri.EscapeDataString(domain)}&sz=128",
            $"https://icons.duckduckgo.com/ip3/{Uri.EscapeDataString(domain)}.ico",
            $"https://{domain}/apple-touch-icon.png",
            $"https://{domain}/favicon.ico"
        ];

        foreach (var candidateUrl in candidates)
        {
            var bytes = await TryDownloadValidImageAsync(candidateUrl, ct);
            if (bytes is not null)
                return bytes;
        }

        return null;
    }

    /// <summary>
    /// 直接从公开图片 URL 下载并验证图片。
    /// </summary>
    public static Task<byte[]?> FetchByImageUrlAsync(string imageUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageUrl)) return Task.FromResult<byte[]?>(null);
        return TryDownloadValidImageAsync(imageUrl.Trim(), ct);
    }

    /// <summary>
    /// 将图标字节写入数据库 CustomIcons（按 SHA-256 自动去重，避免重复存储同一网站图标）。
    /// </summary>
    public static Guid AddOrReuseCustomIcon(KdbxDatabase db, string name, byte[] imageBytes)
    {
        var hash = SHA256.HashData(imageBytes);
        foreach (var existing in db.Meta.CustomIcons)
        {
            if (existing.Data.Length == imageBytes.Length &&
                SHA256.HashData(existing.Data).AsSpan().SequenceEqual(hash))
            {
                return existing.Uuid;
            }
        }

        var icon = new KdbxCustomIcon
        {
            Uuid = Guid.NewGuid(),
            Name = name,
            Data = imageBytes,
            LastModificationTime = DateTimeOffset.UtcNow
        };
        db.Meta.CustomIcons.Add(icon);
        db.IsModified = true;
        return icon.Uuid;
    }

    /// <summary>
    /// 从数据库 CustomIcons 列表安全解码指定 UUID 的位图。
    /// </summary>
    public static Bitmap? TryDecodeCustomIcon(KdbxDatabase? db, Guid customIconUuid)
    {
        if (db is null || customIconUuid == Guid.Empty) return null;
        var icon = db.Meta.CustomIcons.FirstOrDefault(i => i.Uuid == customIconUuid);
        if (icon is null || icon.Data.Length == 0) return null;

        try
        {
            using var ms = new MemoryStream(icon.Data);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryDownloadValidImageAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < 32 || bytes.Length > 2 * 1024 * 1024) return null;

            // 校验字节流确实能被 Avalonia Bitmap 解码，避免存入 HTML 404 页面
            using var ms = new MemoryStream(bytes);
            using var bmp = new Bitmap(ms);
            if (bmp.PixelSize.Width <= 0 || bmp.PixelSize.Height <= 0) return null;

            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
