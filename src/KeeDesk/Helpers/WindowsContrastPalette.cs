using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace KeeDesk.Helpers;

internal static class WindowsContrastPalette
{
    private static readonly Dictionary<string, (bool Exists, object? Value)> PreviousResources = new();

    public static bool TryApply(Application app)
    {
        var resources = app.Resources;
        foreach (var (key, previous) in PreviousResources)
        {
            if (previous.Exists) resources[key] = previous.Value;
            else resources.Remove(key);
        }
        PreviousResources.Clear();

        if (!OperatingSystem.IsWindows() ||
            app.PlatformSettings?.GetColorValues().ContrastPreference != ColorContrastPreference.High)
            return false;

        void Set(string key, object value)
        {
            bool existed = resources.TryGetValue(key, out var previous);
            PreviousResources.Add(key, (existed, previous));
            resources[key] = value;
        }

        void Add(int systemColor, params string[] keys)
        {
            uint value = GetSysColor(systemColor);
            var brush = new SolidColorBrush(Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16)));
            foreach (string key in keys) Set(key, brush);
        }

        Add(5, "TitleBarBackgroundBrush", "SidebarBackgroundBrush", "ListPaneBackgroundBrush", "WorkspaceBackgroundBrush",
            "DetailBackgroundBrush", "CardBackgroundBrush", "CardSubtleBackgroundBrush", "InputBackgroundBrush",
            "InputFocusedBackgroundBrush", "ToastBackgroundBrush", "MenuGlassBrushWindows", "WorkspaceMenuBackgroundBrush", "AttentionBackgroundBrush",
            "BadgePillBackgroundBrush", "MicaTintBrush", "FlyoutSurfaceBrush");
        Add(8, "PrimaryTextBrush", "SecondaryTextBrush", "ToastForegroundBrush", "CardBorderBrush", "DividerBrush",
            "InputBorderBrush", "StandardButtonBorderBrush", "MenuSurfaceStrokeBrush", "MenuFlyoutPresenterBorderBrush",
            "BadgePillForegroundBrush", "WarningBrush", "DangerBrush", "SuccessBrush", "ProgressTrackBrush", "ControlFocusBrush",
            "ControlAccessibleStrokeBrush", "ControlAccessibleStrokeHoverBrush", "ControlAccessibleStrokePressedBrush",
            "StandardButtonHoverBorderBrush", "StandardButtonPressedBorderBrush");
        Add(17, "ControlDisabledForegroundBrush", "ControlDisabledBorderBrush");
        Add(13, "AccentButtonBackgroundBrush", "AccentButtonHoverBackgroundBrush", "AccentButtonPressedBackgroundBrush",
            "SystemControlHighlightAccentBrush", "SystemControlBackgroundAccentBrush", "SystemAccentColorBrush", "BrandEmeraldBrush");
        Add(14, "AccentActionForegroundBrush", "AccentActionHoverForegroundBrush", "AccentActionPressedForegroundBrush");
        Add(26, "AccentTextBrush", "SystemControlForegroundAccentBrush");
        Add(15, "StandardButtonBackgroundBrush", "StandardButtonHoverBackgroundBrush", "StandardButtonPressedBackgroundBrush",
            "EntryRowHoverBackgroundBrush", "EntryRowSelectedBackgroundBrush", "SidebarItemHoverBackgroundBrush",
            "SidebarItemSelectedBackgroundBrush", "OptionCardSelectedBackgroundBrush", "ControlDisabledBackgroundBrush");
        Set("PickerShadow", default(BoxShadows));
        var focus = (SolidColorBrush)resources["AccentButtonBackgroundBrush"]!;
        Set("TextControlFocusBottomShadow", new BoxShadows(new BoxShadow
        {
            IsInset = true,
            OffsetY = -2.5,
            Color = focus.Color,
        }));
        return true;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetSysColor(int index);
}
