using System.Diagnostics;

namespace KeeDesk.Services;

/// <summary>
/// 跨平台原生系统通知服务（macOS 通知中心 / Linux notify-send / Windows 通知），零额外依赖。
/// </summary>
public static class NativeNotificationService
{
    public static void Send(string title, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        _ = Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    var safeTitle = EscapeAppleScript(title);
                    var safeMsg = EscapeAppleScript(message);
                    var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add($"display notification \"{safeMsg}\" with title \"{safeTitle}\"");
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);
                }
                else if (OperatingSystem.IsLinux())
                {
                    var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
                    psi.ArgumentList.Add(title);
                    psi.ArgumentList.Add(message);
                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);
                }
            }
            catch { }
        });
    }

    private static string EscapeAppleScript(string input)
        => input.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");
}
