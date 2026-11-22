using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using KeePassModern.Helpers;
using KeePassModern.Services;
using KeePassModern.ViewModels;
using KeePassModern.Views;

namespace KeePassModern;

public partial class App : Application
{
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        var zhCulture = new CultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentCulture = zhCulture;
        CultureInfo.DefaultThreadCurrentUICulture = zhCulture;
        CultureInfo.CurrentCulture = zhCulture;
        CultureInfo.CurrentUICulture = zhCulture;

        AvaloniaXamlLoader.Load(this);
        Resources["DesktopFontFamily"] = OperatingSystem.IsWindows()
            ? new Avalonia.Media.FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI")
            : Avalonia.Media.FontFamily.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var settingsService = new AppSettingsService();
        LocalizationService.Apply(settingsService.Settings.Language);
        ThemeAccentService.ApplyThemeVariant(settingsService.Settings.ThemeMode);
        ThemeAccentService.ApplyAccent(settingsService.Settings.AccentMode, settingsService.Settings.CustomAccentHex);
        DensityService.Apply(settingsService.Settings.UiDensity);

        void UpdateAccent() => ThemeAccentService.ApplyAccent(settingsService.Settings.AccentMode, settingsService.Settings.CustomAccentHex);
        ActualThemeVariantChanged += (_, _) => UpdateAccent();
        if (PlatformSettings is { } platformSettings)
            platformSettings.ColorValuesChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateAccent);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(settingsService);
            DataContext = vm;
            var mainWindow = new MainWindow { DataContext = vm };
            desktop.MainWindow = mainWindow;

            SetupTrayIcon(desktop, mainWindow, vm);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 初始化跨平台系统托盘图标与快捷菜单（显示窗口 / 锁定全部密码库 / 退出）。
    /// </summary>
    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop, MainWindow mainWindow, MainWindowViewModel vm)
    {
        try
        {
            var iconPath = OperatingSystem.IsMacOS()
                ? "avares://KeePassModern/Assets/Branding/Tray/macos.png"
                : "avares://KeePassModern/Assets/Branding/Tray/windows.png";

            using var stream = AssetLoader.Open(new Uri(iconPath));
            var menu = new NativeMenu();

            var showItem = new NativeMenuItem("显示 KeePassModern");
            showItem.Click += (_, _) => RestoreMainWindow(mainWindow);
            menu.Add(showItem);

            var lockItem = new NativeMenuItem("锁定全部密码库");
            lockItem.Click += (_, _) =>
            {
                if (vm.IsUnlocked) vm.LockVaultCommand.Execute(null);
            };
            menu.Add(lockItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("退出 KeePassModern");
            exitItem.Click += (_, _) =>
            {
                if (vm.IsUnlocked) vm.LockVaultCommand.Execute(null);
                _trayIcon?.Dispose();
                desktop.Shutdown();
            };
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = "KeePassModern 密码管理器",
                Menu = menu,
                IsVisible = true
            };
            if (OperatingSystem.IsMacOS())
                MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
            _trayIcon.Clicked += (_, _) => RestoreMainWindow(mainWindow);

            var icons = new TrayIcons { _trayIcon };
            TrayIcon.SetIcons(this, icons);
        }
        catch { }
    }

    private static void RestoreMainWindow(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
