using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace KeeDesk.Controls;

internal sealed class PickerSurface : Border
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        AllowOverlayShadow(this);
    }

    internal static void AttachToFluent2Templates()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<CalendarDatePicker>((_, args) => Attach(args));
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ComboBox>((_, args) => Attach(args));

        static void Attach(TemplateAppliedEventArgs args)
        {
            if (args.NameScope.Find<Popup>("PART_Popup") is { } popup)
                popup.Opened += (_, _) => AllowOverlayShadow(popup.Child);
        }
    }

    private static void AllowOverlayShadow(Visual? surface)
    {
        var overlay = surface?.FindAncestorOfType<OverlayPopupHost>();
        if (overlay is null)
            return;

        // The popup host and its layout transform clip to the panel's layout bounds by default.
        overlay.SetCurrentValue(ClipToBoundsProperty, false);
        surface?.FindAncestorOfType<LayoutTransformControl>()?.SetCurrentValue(ClipToBoundsProperty, false);
    }
}
