using Avalonia;
using Avalonia.Media;
using KeeDesk.Helpers;

namespace KeeDesk;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var windowsOptions = OperatingSystem.IsWindows() ? new Win32PlatformOptions() : null;
        var builder = AppBuilder.Configure(() => new App { WindowsOptions = windowsOptions })
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .With(new MacOSPlatformOptions
            {
                DisableDefaultApplicationMenuItems = false,
                DisableSetProcessName = true,
                DisableAvaloniaAppDelegate = false
            });

        if (windowsOptions is not null)
            builder.With(windowsOptions);

        if (OperatingSystem.IsLinux())
        {
            builder.With(new FontManagerOptions
            {
                DefaultFamilyName = "fonts:Inter#Inter"
            });
        }

        if (DesktopWindowChrome.LinuxButtonSide is not null)
        {
#pragma warning disable AVALONIA_X11_CSD
            builder.With(new X11PlatformOptions { EnableDrawnDecorations = true });
#pragma warning restore AVALONIA_X11_CSD
        }

        return builder;
    }
}
