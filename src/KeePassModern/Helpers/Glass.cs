using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;

namespace KeePassModern.Helpers;

/// <summary>
/// 原生磨砂玻璃弹窗与右键菜单支持（参考 KanTuNext Services/Glass.cs）：
/// 在 macOS 上使用系统原生 NSVisualEffectMaterialMenu (material=5) 并裁剪圆角；
/// 在 Windows 11 上通过 DWM DWMWA_WINDOW_CORNER_PREFERENCE 启用系统级圆角与亚克力半透明染色；
/// 在不支持透明模糊的平台上自动保持不透明卡片外观。
/// </summary>
public static class Glass
{
    private static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static void Attach(TemplatedControl menu)
    {
        if (!Supported) return;
        menu.AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(menu) is not PopupRoot root) return;
            root.Background = Brushes.Transparent;
            root.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur];
            Frost(root, menu);
        };
        menu.Loaded += (_, _) =>
        {
            if (TopLevel.GetTopLevel(menu) is PopupRoot root)
                Frost(root, menu);
        };
    }

    private static void Frost(PopupRoot root, TemplatedControl menu)
    {
        if (root.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur
            && root.ActualTransparencyLevel != WindowTransparencyLevel.Blur)
            return;

        if (!menu.Classes.Contains("glass"))
            menu.Classes.Add("glass");

        if (OperatingSystem.IsWindows())
        {
            if (!menu.Classes.Contains("windows"))
                menu.Classes.Add("windows");
            menu.CornerRadius = new Avalonia.CornerRadius(8);
        }
        Round(root, menu);
    }

    private static void Round(PopupRoot root, TemplatedControl menu)
    {
        if (root.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;
        try
        {
            if (OperatingSystem.IsMacOS())
                MacRound(handle, menu.CornerRadius.TopLeft > 0 ? menu.CornerRadius.TopLeft : 6, menu.ActualThemeVariant == ThemeVariant.Dark);
            else
                WindowsRound(handle);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private const string ObjcLib = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjcLib)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjcLib)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendString(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void SendNint(IntPtr receiver, IntPtr selector, nint arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIndex(IntPtr receiver, IntPtr selector, nuint index);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void SendDouble(IntPtr receiver, IntPtr selector, double arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool arg);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SendBoolResult(IntPtr receiver, IntPtr selector, IntPtr arg);

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static void MacRound(IntPtr window, double radius, bool dark)
    {
        SendBool(window, Sel("setOpaque:"), false);
        Send(window, Sel("setBackgroundColor:"), Send(objc_getClass("NSColor"), Sel("clearColor")));
        var appearance = Send(objc_getClass("NSAppearance"), Sel("appearanceNamed:"),
            SendString(objc_getClass("NSString"), Sel("stringWithUTF8String:"), dark ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua"));
        Send(window, Sel("setAppearance:"), appearance);

        var content = Send(window, Sel("contentView"));
        if (content == IntPtr.Zero) return;
        SendBool(content, Sel("setWantsLayer:"), true);
        var layer = Send(content, Sel("layer"));
        if (layer != IntPtr.Zero)
        {
            SendDouble(layer, Sel("setCornerRadius:"), radius);
            SendBool(layer, Sel("setMasksToBounds:"), true);
        }

        foreach (var view in EffectViews(content))
        {
            SendNint(view, Sel("setMaterial:"), 5); // NSVisualEffectMaterialMenu
            SendNint(view, Sel("setState:"), 1); // NSVisualEffectStateActive
            SendBool(view, Sel("setWantsLayer:"), true);
            if (Send(view, Sel("layer")) is var viewLayer && viewLayer != IntPtr.Zero)
            {
                SendDouble(viewLayer, Sel("setCornerRadius:"), radius);
                SendBool(viewLayer, Sel("setMasksToBounds:"), true);
            }
        }
        SendBool(window, Sel("setHasShadow:"), true);
        Send(window, Sel("invalidateShadow"));
    }

    private static List<IntPtr> EffectViews(IntPtr content)
    {
        var effectClass = objc_getClass("NSVisualEffectView");
        var subviews = Send(content, Sel("subviews"));
        var count = (nint)Send(subviews, Sel("count"));
        var views = new List<IntPtr>();
        for (nuint i = 0; i < (nuint)count; i++)
        {
            var view = SendIndex(subviews, Sel("objectAtIndex:"), i);
            if (SendBoolResult(view, Sel("isKindOfClass:"), effectClass)) views.Add(view);
        }
        return views;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void WindowsRound(IntPtr hwnd)
    {
        var round = 2; // DWMWCP_ROUND: 8px
        DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }
}

/// <summary>
/// 带原生磨砂玻璃效果的右键菜单 ContextMenu。
/// 注意：在 Avalonia 12 中，继承自 ContextMenu 的子类必须显式重写 StyleKeyOverride => typeof(ContextMenu)，
/// 否则 FluentTheme 找不到 GlassContextMenu 的 ControlTemplate，甚至会把父容器渲染为空白！
/// </summary>
public sealed class GlassContextMenu : ContextMenu
{
    protected override Type StyleKeyOverride => typeof(ContextMenu);

    public GlassContextMenu()
    {
        Glass.Attach(this);
    }
}

/// <summary>
/// 带原生磨砂玻璃效果的悬浮菜单 MenuFlyout（用于左下角设置按钮等弹出菜单）。
/// </summary>
public sealed class GlassMenuFlyout : MenuFlyout
{
    protected override Control CreatePresenter()
    {
        var presenter = base.CreatePresenter();
        if (presenter is TemplatedControl menu)
            Glass.Attach(menu);
        return presenter;
    }
}
