using System.Reflection;

namespace KeeDesk.Services;

/// <summary>
/// 应用版本号提供器（兼容 NativeAOT 裁剪环境）。
/// </summary>
public static class AppVersionProvider
{
    public static string GetCurrentVersion()
    {
        var assembly = typeof(AppVersionProvider).Assembly;
        var info = assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(info))
        {
            var versionPart = info.Split('+', 2)[0].TrimStart('v');
            if (Version.TryParse(versionPart, out var parsed))
                return parsed.ToString();
        }

        var v = assembly.GetName().Version;
        if (v is not null)
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build)).ToString();

        return "1.3.6";
    }
}
