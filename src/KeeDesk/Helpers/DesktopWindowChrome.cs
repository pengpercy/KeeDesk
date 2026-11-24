using System.ComponentModel;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace KeeDesk.Helpers;

internal enum CaptionButtonSide
{
    Left,
    Right
}

internal static class DesktopWindowChrome
{
    private static readonly Lazy<CaptionButtonSide> LinuxSide = new(() =>
        DetectLinuxButtonSide() ?? CaptionButtonSide.Right);
    private static bool _linuxChromeAttached;

    public static CaptionButtonSide? LinuxButtonSide => OperatingSystem.IsLinux() ? LinuxSide.Value : null;
    public static bool ButtonsOnLeft => OperatingSystem.IsMacOS() || LinuxButtonSide == CaptionButtonSide.Left;

    internal static void AttachLinuxWindowChrome()
    {
        if (LinuxButtonSide is null || _linuxChromeAttached)
            return;

        _linuxChromeAttached = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<Window>((window, _) => UpdateLinuxWindowChrome(window));
        Control.LoadedEvent.AddClassHandler<Window>((window, _) => UpdateLinuxWindowChrome(window));
        TopLevel.ActualTransparencyLevelProperty.Changed.AddClassHandler<Window>((window, _) => UpdateLinuxWindowChrome(window));
        Window.WindowStateProperty.Changed.AddClassHandler<Window>((window, _) => UpdateLinuxWindowChrome(window));
        Window.ExtendClientAreaToDecorationsHintProperty.Changed.AddClassHandler<Window>((window, _) => UpdateLinuxWindowChrome(window));
    }

    private static void UpdateLinuxWindowChrome(Window window)
    {
        bool csd = window.ExtendClientAreaToDecorationsHint && window.WindowDecorations != WindowDecorations.None;
        window.Classes.Set("linuxCsd", csd);

        if (csd && window.TransparencyLevelHint.Count == 0)
        {
            window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None];
        }

        bool rounded = csd
            && window.ActualTransparencyLevel == WindowTransparencyLevel.Transparent
            && window.WindowState == WindowState.Normal;
        window.CornerRadius = rounded ? new CornerRadius(10) : default;
    }

    private static CaptionButtonSide? DetectLinuxButtonSide()
    {
        var desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
                       ?? Environment.GetEnvironmentVariable("DESKTOP_SESSION")
                       ?? "").ToLowerInvariant();

        if (desktop.Contains("kde") || desktop.Contains("plasma"))
        {
            const string group = "org.kde.kdecoration2";
            var left = ReadConfig("kwinrc", group, "ButtonsOnLeft") ?? "MSE";
            var right = ReadConfig("kwinrc", group, "ButtonsOnRight") ?? "HIAX";
            return GetSide(left, right, "X", "HA");
        }

        if (desktop.Contains("xfce") || desktop.Contains("xubuntu"))
        {
            var layout = ReadSetting("xfconf-query", "-c", "xfwm4", "-p", "/general/button_layout") ?? "O|SHMC";
            var columns = layout.Split('|', 2);
            return GetSide(columns[0], columns.Length == 2 ? columns[1] : "", "C", "HM");
        }

        if ((desktop.Contains("openbox") || desktop.Contains("lxde")) &&
            ReadOpenboxButtonSide() is { } openboxSide)
            return openboxSide;

        var schema = desktop switch
        {
            var name when name.Contains("cinnamon") => "org.cinnamon.desktop.wm.preferences",
            var name when name.Contains("mate") => "org.mate.Marco.general",
            var name when name.Contains("pantheon") => "org.pantheon.desktop.gala.appearance",
            var name when name.Contains("gnome") || name.Contains("unity") || name.Contains("ubuntu") =>
                "org.gnome.desktop.wm.preferences",
            _ => null
        };
        var preference = schema is null ? null : ReadSetting("gsettings", "get", schema, "button-layout");
        return ParseGSettingsLayout(
            preference
            ?? ReadConfig("gtk-4.0/settings.ini", "Settings", "gtk-decoration-layout")
            ?? ReadConfig("gtk-3.0/settings.ini", "Settings", "gtk-decoration-layout"));
    }

    private static CaptionButtonSide? ReadOpenboxButtonSide()
    {
        const string layoutFile = "openbox/rc.xml";
        foreach (var path in new[] { Path.Combine(ConfigHome, layoutFile), Path.Combine("/etc/xdg", layoutFile) })
        {
            try
            {
                var theme = XDocument.Load(path).Root?.Elements()
                    .FirstOrDefault(element => element.Name.LocalName == "theme");
                var layout = theme?.Elements().FirstOrDefault(element => element.Name.LocalName == "titleLayout")?.Value;
                if (ParseOpenboxLayout(layout) is { } side)
                    return side;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
        }
        return null;
    }

    internal static CaptionButtonSide? ParseOpenboxLayout(string? layout)
    {
        if (layout is null || layout.IndexOf('L') is not (var label and >= 0))
            return null;
        return GetSide(layout[..label], layout[(label + 1)..], "C", "IM");
    }

    internal static CaptionButtonSide? ParseGSettingsLayout(string? layout)
    {
        if (layout is null) return null;
        var sides = layout.Trim().Trim('\'', '"').Split(':', 2);
        const string close = "close";
        const string resize = "minimize,maximize";
        return GetSide(sides[0], sides.Length == 2 ? sides[1] : "", close, resize, commaSeparated: true);
    }

    // Use the close button as the anchor when a desktop splits the controls between sides.
    private static CaptionButtonSide? GetSide(string left, string right, string close, string secondary, bool commaSeparated = false)
    {
        var leftButtons = GetButtons(left);
        var rightButtons = GetButtons(right);
        if (leftButtons.Contains(close)) return CaptionButtonSide.Left;
        if (rightButtons.Contains(close)) return CaptionButtonSide.Right;

        var resizeButtons = GetButtons(secondary);
        var leftCount = leftButtons.Intersect(resizeButtons).Count();
        var rightCount = rightButtons.Intersect(resizeButtons).Count();
        if (leftCount + rightCount == 0) return null;
        return leftCount > rightCount ? CaptionButtonSide.Left : CaptionButtonSide.Right;

        IEnumerable<string> GetButtons(string buttons) => commaSeparated
            ? buttons.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : buttons.Select(button => button.ToString());
    }

    private static string ConfigHome => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configHome
        ? configHome
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    private static string? ReadConfig(string file, string group, string key)
    {
        try
        {
            var inGroup = false;
            foreach (var entry in File.ReadLines(Path.Combine(ConfigHome, file)))
            {
                var line = entry.Trim();
                if (line.StartsWith('['))
                    inGroup = line == $"[{group}]";
                else if (inGroup && line.IndexOf('=') is var separator && separator >= 0 && line[..separator].Trim() == key)
                    return line[(separator + 1)..].Trim();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return null;
    }

    private static string? ReadSetting(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(400))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 && Task.WaitAll([output, error], 50) ? output.Result.Trim() : null;
        }
        catch (Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (AggregateException) { return null; }
    }
}
