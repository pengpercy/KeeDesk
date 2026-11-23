using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using KeeDesk.Helpers;
using KeeDesk.Services;
using KeeDesk.ViewModels;
using KeeDesk.Views;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk;

public partial class App : Application
{
    private TrayIcon? _trayIcon;
    internal Win32PlatformOptions? WindowsOptions { get; init; }
    internal WindowsCaptureProtection? CaptureProtection { get; private set; }

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
        if (OperatingSystem.IsWindows())
            Styles.Add((Styles)this.FindResource("WindowsWindowDecorations")!);

        if (DesktopWindowChrome.LinuxButtonSide is { } side)
        {
            var key = side == CaptionButtonSide.Left ? "LinuxCaptionOnLeft" : "LinuxCaptionOnRight";
            Styles.Add(new Style(selector => selector.Is<Window>())
            {
                Setters = { new Setter(Window.WindowDecorationsThemeProperty, this.FindResource(key)) }
            });
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var settingsService = new AppSettingsService();
        LocalizationService.Apply(settingsService.Settings.Language);
        if (OperatingSystem.IsMacOS() && NativeMenu.GetMenu(this) is { } applicationMenu)
            MacMenuLocalization.Attach(applicationMenu);
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
            if (WindowsOptions is { } windowsOptions)
            {
                var protection = new WindowsCaptureProtection(desktop, windowsOptions,
                    settingsService.Settings.ExcludeWindowsFromCapture);
                CaptureProtection = protection;
                protection.StatusChanged += () =>
                {
                    if (protection.HasFailures)
                        vm.ShowToast(L("Str_WindowCaptureProtectionFailed"), ToastSeverity.Error);
                };
            }
            DataContext = vm;
            var mainWindow = new MainWindow(settingsService) { DataContext = vm };
            desktop.MainWindow = mainWindow;
            LanguageChanged += () =>
            {
                vm.RefreshLanguage();
                mainWindow.RefreshSearchResultMessage();
            };

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
                ? "avares://KeeDesk/Assets/Branding/Tray/macos.png"
                : "avares://KeeDesk/Assets/Branding/Tray/windows.png";

            using var stream = AssetLoader.Open(new Uri(iconPath));
            var menu = new NativeMenu();

            var showItem = new NativeMenuItem(L("Str_ShowKeeDesk"));
            showItem.Click += (_, _) => RestoreMainWindow(mainWindow);
            menu.Add(showItem);

            var lockItem = new NativeMenuItem(L("Str_LockAllVaults"));
            lockItem.Click += (_, _) =>
            {
                if (vm.IsUnlocked) vm.LockVaultCommand.Execute(null);
            };
            menu.Add(lockItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem(L("Str_QuitKeeDesk"));
            exitItem.Click += (_, _) =>
            {
                if (vm.TrySaveBeforeClosing() && desktop.TryShutdown())
                    _trayIcon?.Dispose();
                else
                    RestoreMainWindow(mainWindow);
            };
            menu.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(stream),
                ToolTipText = L("Str_KeeDeskPasswordManager"),
                Menu = menu,
                IsVisible = true
            };
            LanguageChanged += () =>
            {
                showItem.Header = L("Str_ShowKeeDesk");
                lockItem.Header = L("Str_LockAllVaults");
                exitItem.Header = L("Str_QuitKeeDesk");
                _trayIcon.ToolTipText = L("Str_KeeDeskPasswordManager");
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
