using Avalonia;
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
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .With(new MacOSPlatformOptions
            {
                DisableDefaultApplicationMenuItems = false,
                DisableSetProcessName = true,
                DisableAvaloniaAppDelegate = false
            });

        if (DesktopWindowChrome.LinuxButtonSide is not null)
        {
#pragma warning disable AVALONIA_X11_CSD
            builder.With(new X11PlatformOptions { EnableDrawnDecorations = true });
#pragma warning restore AVALONIA_X11_CSD
        }

        return builder;
    }
}
