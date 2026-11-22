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

        var overlay = this.FindAncestorOfType<OverlayPopupHost>();
        if (overlay is null)
            return;

        // The popup host and its layout transform clip to the panel's layout bounds by default.
        overlay.SetCurrentValue(ClipToBoundsProperty, false);
        this.FindAncestorOfType<LayoutTransformControl>()?.SetCurrentValue(ClipToBoundsProperty, false);
    }
}
