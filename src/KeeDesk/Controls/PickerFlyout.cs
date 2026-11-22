using System.ComponentModel;
using Avalonia.Controls;

namespace KeeDesk.Controls;

internal sealed class PickerFlyout : Flyout
{
    protected override void OnOpening(CancelEventArgs args)
    {
        Popup.ShouldUseOverlayLayer = true;
        base.OnOpening(args);
    }
}
