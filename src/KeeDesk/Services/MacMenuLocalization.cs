using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Services;

internal static class MacMenuLocalization
{
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
}
