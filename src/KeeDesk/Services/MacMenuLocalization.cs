using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Services;

internal static class MacMenuLocalization
{
    public static void AttachWindowMenu(Window window)
    {
        if (!OperatingSystem.IsMacOS() || NativeMenu.GetMenu(window) is not { } menu) return;

        // AppKit installs the window's menu when it becomes active. Defer until that
        // installation and the managed header bindings have both had a chance to run.
        void Refresh() => Dispatcher.UIThread.Post(() =>
        {
            if (window.IsVisible)
                RefreshMenuBarTitles(menu);
        }, DispatcherPriority.Background);

        LanguageChanged += Refresh;
        window.Activated += (_, _) => Refresh();
        window.Closed += (_, _) => LanguageChanged -= Refresh;
    }

    public static void Attach(NativeMenu menu)
    {
        // Avalonia's exporter owns this menu and appends the system actions to it.
        // Update the existing items, including Services, to preserve their native callbacks.
        void Refresh()
        {
            foreach (var item in menu.Items.OfType<NativeMenuItem>())
                Translate(item);
        }

        Refresh();
        if (menu.Items is INotifyCollectionChanged items)
        {
            items.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is null) return;
                foreach (var item in e.NewItems.OfType<NativeMenuItem>())
                    Translate(item);
            };
        }

        LanguageChanged += Refresh;
    }

    private static void Translate(NativeMenuItem item)
    {
        string? key = item.Header switch
        {
            "Services" or "服务" => "Str_Services",
            "Hide KeeDesk" or "隐藏 KeeDesk" => "Str_HideKeeDesk",
            "Hide Others" or "隐藏其他" => "Str_HideOthers",
            "Show All" or "全部显示" => "Str_ShowAll",
            "Quit" or "Quit KeeDesk" or "退出 KeeDesk" => "Str_QuitKeeDesk",
            _ => null
        };

        if (key is null) return;
        item.Header = L(key);
        if (key == "Str_HideOthers")
            item.Gesture = new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt);
    }

    private static void RefreshMenuBarTitles(NativeMenu menu)
    {
        var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var menuBar = Send(app, sel_registerName("mainMenu"));
        var count = Send(menuBar, sel_registerName("numberOfItems"));
        var itemAtIndex = sel_registerName("itemAtIndex:");
        var submenuSelector = sel_registerName("submenu");
        var titleSelector = sel_registerName("title");
        var utf8String = sel_registerName("UTF8String");
        var setTitle = sel_registerName("setTitle:");
        var itemChanged = sel_registerName("itemChanged:");

        // Avalonia updates NSMenuItem.title but only sets NSMenu.title when exporting
        // the submenu. The menu bar displays the latter. Index 0 is the app menu.
        for (nint i = 1; i < count; i++)
        {
            var item = Send(menuBar, itemAtIndex, i);
            var submenu = Send(item, submenuSelector);
            if (submenu == 0) continue;

            var title = Send(item, titleSelector);
            var header = Marshal.PtrToStringUTF8(Send(title, utf8String));
            if (!menu.Items.Any(entry => entry is NativeMenuItem { Menu: not null } group && group.Header == header))
                continue;

            if (header == Marshal.PtrToStringUTF8(Send(Send(submenu, titleSelector), utf8String)))
                continue;

            SendVoid(submenu, setTitle, title);
            SendVoid(menuBar, itemChanged, item);
        }
    }

    private const string ObjcLib = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjcLib)]
    private static extern nint objc_getClass(string name);

    [DllImport(ObjcLib)]
    private static extern nint sel_registerName(string name);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint value);

    [DllImport(ObjcLib, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector, nint value);
}
