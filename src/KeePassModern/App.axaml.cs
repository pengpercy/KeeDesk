using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KeePassModern.Helpers;
using KeePassModern.Services;
using KeePassModern.ViewModels;
using KeePassModern.Views;

namespace KeePassModern;

public partial class App : Application
{
    public override void Initialize()
    {
        var zhCulture = new CultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentCulture = zhCulture;
        CultureInfo.DefaultThreadCurrentUICulture = zhCulture;
        CultureInfo.CurrentCulture = zhCulture;
        CultureInfo.CurrentUICulture = zhCulture;

        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var settingsService = new AppSettingsService();
        ThemeAccentService.ApplyThemeVariant(settingsService.Settings.ThemeMode);
        ThemeAccentService.ApplyAccent(settingsService.Settings.AccentMode, settingsService.Settings.CustomAccentHex);
        DensityService.Apply(settingsService.Settings.UiDensity);

        ActualThemeVariantChanged += (_, _) =>
        {
            ThemeAccentService.ApplyAccent(settingsService.Settings.AccentMode, settingsService.Settings.CustomAccentHex);
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel(settingsService);
            DataContext = vm;
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
