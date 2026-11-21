using Avalonia;

namespace KeePassModern;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .With(new MacOSPlatformOptions
            {
                DisableDefaultApplicationMenuItems = false,
                DisableSetProcessName = true,
                DisableAvaloniaAppDelegate = false
            });
}
