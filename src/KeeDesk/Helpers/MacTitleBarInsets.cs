using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace KeeDesk.Helpers;

/// <summary>
/// macOS 原生标题栏与交通灯位置适配工具（参考 KanTu 实践，兼容 x64 与 arm64）。
/// AppKit 默认按 28pt 标题栏排布交通灯，本类将交通灯垂直并对称居中到更高的现代标题栏中，
/// 并在窗口缩放/激活/主题切换导致 AppKit 重置布局时自动重新校准。
/// </summary>
public static class MacTitleBarInsets
{
    private const string ObjcLib = "/usr/lib/libobjc.A.dylib";

    private enum NSWindowButton : int
    {
        Close = 0,
        Miniaturize = 1,
        Zoom = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X, Y;
    }

    [DllImport(ObjcLib)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_CGPoint(IntPtr receiver, IntPtr selector, CGPoint arg1);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_CGRect(IntPtr receiver, IntPtr selector, CGRect arg1);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr_nint(IntPtr receiver, IntPtr selector, nint arg1);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern CGRect objc_msgSend_CGRect(IntPtr receiver, IntPtr selector);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend_stret")]
    private static extern void objc_msgSend_stret(out CGRect ret, IntPtr receiver, IntPtr selector);

    /// <summary>
    /// 为 macOS 窗口自动绑定交通灯居中与右下微调逻辑，覆盖打开、缩放、激活、状态变更与明暗主题切换事件。
    /// </summary>
    public static void AttachTrafficLightsCentering(Window window, double titleBarHeight = 48, Action<double>? onRightUpdated = null)
    {
        if (!OperatingSystem.IsMacOS()) return;

        void Recenter()
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            TryCenterTrafficLights(handle, titleBarHeight);
            if (onRightUpdated is not null && TryGetTrafficLightsRight(handle, out var right))
                onRightUpdated(right);
        }

        window.Opened += (_, _) => Recenter();
        window.Resized += (_, _) => Recenter();
        window.Activated += (_, _) => Recenter();
        window.ScalingChanged += (_, _) => Recenter();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty) Recenter();
        };
        window.ActualThemeVariantChanged += (_, _) =>
            Dispatcher.UIThread.Post(Recenter, DispatcherPriority.Background);
    }

    public static bool TryGetTrafficLightsRight(IntPtr nsWindowHandle, out double right)
    {
        right = 0;
        if (nsWindowHandle == IntPtr.Zero) return false;
        try
        {
            var selBtn = sel_registerName("standardWindowButton:");
            var selFrame = sel_registerName("frame");
            var zoomBtn = objc_msgSend_IntPtr_nint(nsWindowHandle, selBtn, (nint)NSWindowButton.Zoom);
            if (zoomBtn == IntPtr.Zero) return false;

            var rect = GetFrame(zoomBtn, selFrame);
            if (rect.Width <= 0) return false;
            right = rect.X + rect.Width;
            return right > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将 macOS 交通灯按钮按目标标题栏高度向右下方向对称微调居中，同时保持 NSTitlebarView 原有命中测试高度不变。
    /// </summary>
    public static bool TryCenterTrafficLights(IntPtr nsWindowHandle, double titleBarHeight)
    {
        if (nsWindowHandle == IntPtr.Zero) return false;

        try
        {
            var selStandardWindowButton = sel_registerName("standardWindowButton:");
            var selFrame = sel_registerName("frame");
            var selSuperview = sel_registerName("superview");
            var selSetFrameOrigin = sel_registerName("setFrameOrigin:");
            var selSetFrame = sel_registerName("setFrame:");

            var buttons = new[]
            {
                objc_msgSend_IntPtr_nint(nsWindowHandle, selStandardWindowButton, (nint)NSWindowButton.Close),
                objc_msgSend_IntPtr_nint(nsWindowHandle, selStandardWindowButton, (nint)NSWindowButton.Miniaturize),
                objc_msgSend_IntPtr_nint(nsWindowHandle, selStandardWindowButton, (nint)NSWindowButton.Zoom)
            };
            if (Array.IndexOf(buttons, IntPtr.Zero) >= 0) return false;

            // 层级链：closeButton -> NSTitlebarView -> NSTitlebarContainerView -> NSThemeFrame
            var titlebarView = objc_msgSend_IntPtr(buttons[0], selSuperview);
            var container = titlebarView == IntPtr.Zero ? IntPtr.Zero : objc_msgSend_IntPtr(titlebarView, selSuperview);
            var themeFrame = container == IntPtr.Zero ? IntPtr.Zero : objc_msgSend_IntPtr(container, selSuperview);
            if (themeFrame == IntPtr.Zero) return false;

            var titlebarFrame = GetFrame(titlebarView, selFrame);
            var themeFrameRect = GetFrame(themeFrame, selFrame);
            var closeFrame = GetFrame(buttons[0], selFrame);
            var spacing = GetFrame(buttons[1], selFrame).X - closeFrame.X;
            if (titlebarFrame.Height <= 0 || closeFrame.Height <= 0 || spacing <= 0) return false;

            // 向下扩展容器保持顶缘高光，并将 titlebarView 置底以维持 Avalonia 标题栏拖动命中区
            var drop = Math.Max(0, (titleBarHeight - titlebarFrame.Height) / 2);
            var containerHeight = titlebarFrame.Height + drop;
            objc_msgSend_void_CGRect(container, selSetFrame, new CGRect
            {
                X = 0,
                Y = themeFrameRect.Height - containerHeight,
                Width = themeFrameRect.Width,
                Height = containerHeight
            });
            objc_msgSend_void_CGRect(titlebarView, selSetFrame, new CGRect
            {
                Width = themeFrameRect.Width,
                Height = titlebarFrame.Height
            });

            var left = Math.Max(0, (titleBarHeight - closeFrame.Height) / 2);
            for (var i = 0; i < buttons.Length; i++)
            {
                var frame = GetFrame(buttons[i], selFrame);
                objc_msgSend_void_CGPoint(buttons[i], selSetFrameOrigin, new CGPoint { X = left + i * spacing, Y = frame.Y });
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static CGRect GetFrame(IntPtr view, IntPtr selFrame)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            objc_msgSend_stret(out var rect, view, selFrame);
            return rect;
        }

        return objc_msgSend_CGRect(view, selFrame);
    }
}
