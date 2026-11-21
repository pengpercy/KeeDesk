using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using KeePassModern.Helpers;
using KeePassModern.Services;

namespace KeePassModern.Views;

/// <summary>
/// 自动更新升级弹窗：展示 Release 更新日志、下载进度并调用 UpdateApplier 执行跨平台替换重启。
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly ReleaseInfo _release;
    private readonly UpdateService _updateService = new();
    private bool _isDownloading;

    public UpdateWindow() : this(new ReleaseInfo()) { }

    public UpdateWindow(ReleaseInfo release)
    {
        InitializeComponent();
        MacTitleBarInsets.AttachTrafficLightsCentering(this, 42);
        _release = release;

        var current = AppVersionProvider.GetCurrentVersion();
        TitleText.Text = $"发现 KeePassModern {release.TagName}";
        VersionText.Text = $"当前版本 v{current}  →  最新版本 {release.TagName}";
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(release.Body) ? "包含性能优化与问题修复。" : release.Body.Trim();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private async void DownloadAndApply_Click(object? sender, RoutedEventArgs e)
    {
        if (_isDownloading) return;

        var asset = UpdateAssetSelector.SelectPreferredAsset(_release);
        if (asset is null)
        {
            OpenReleasePage_Click(sender, e);
            return;
        }

        _isDownloading = true;
        DownloadBtn.IsEnabled = false;
        ProgressPanel.IsVisible = true;
        StatusText.Text = $"正在下载 {asset.Name}...";

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "KeePassModern", "downloads");
            Directory.CreateDirectory(tempDir);
            var destPath = Path.Combine(tempDir, asset.Name);

            var progress = new Progress<double>(p =>
            {
                DownloadProgress.Value = p * 100;
                StatusText.Text = $"正在下载 {asset.Name} ({p * 100:0}%)...";
            });

            await _updateService.DownloadUpdateAsync(asset.BrowserDownloadUrl, destPath, progress);
            StatusText.Text = "下载完成，正在准备应用更新...";

            var applied = await UpdateApplier.TryApplyUpdateAsync(destPath);
            if (applied)
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
                return;
            }

            // 开发调试模式（非 .app / 非打包目录）时，直接打开下载目录供手动安装
            StatusText.Text = "已下载完成，正在打开所在目录...";
            Process.Start(new ProcessStartInfo(tempDir) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"下载更新失败：{ex.Message}";
            DownloadBtn.IsEnabled = true;
            _isDownloading = false;
        }
    }

    private void OpenReleasePage_Click(object? sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(_release.HtmlUrl)
            ? "https://github.com/pengpercy/KeePassModern/releases"
            : _release.HtmlUrl;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
