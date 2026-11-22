using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using KeeDesk.Helpers;

namespace KeeDesk.Controls;

internal sealed class ToastPresence : Border
{
    public static readonly StyledProperty<bool> IsShownProperty =
        AvaloniaProperty.Register<ToastPresence, bool>(nameof(IsShown));

    private static readonly StyledProperty<double> RevealProperty =
        AvaloniaProperty.Register<ToastPresence, double>("Reveal");

    private static readonly SplineEasing EasyEase = new() { X1 = 0.33, Y1 = 0, X2 = 0.67, Y2 = 1 };
    private readonly DoubleTransition _size = new() { Property = RevealProperty, Easing = EasyEase };
    private readonly DoubleTransition _fade = new() { Property = OpacityProperty, Easing = EasyEase };

    public ToastPresence()
    {
        IsVisible = false;
        Opacity = 0;
        Transitions = new Transitions { _size, _fade };
    }

    public bool IsShown
    {
        get => GetValue(IsShownProperty);
        set => SetValue(IsShownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsShownProperty)
        {
            if (IsShown)
                IsVisible = true;

            bool animate = this.IsAttachedToVisualTree() && !SystemMotionPreferences.ReduceMotion;
            _size.Duration = animate ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero;
            _size.Delay = animate && !IsShown ? TimeSpan.FromMilliseconds(400) : TimeSpan.Zero;
            _fade.Duration = animate ? TimeSpan.FromMilliseconds(400) : TimeSpan.Zero;
            _fade.Delay = animate && IsShown ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero;
            SetValue(RevealProperty, IsShown ? 1 : 0);
            Opacity = IsShown ? 1 : 0;
        }

        if (change.Property == RevealProperty || change.Property == BoundsProperty || change.Property == IsShownProperty)
        {
            double reveal = GetValue(RevealProperty);
            Clip = reveal >= 1 ? null : new RectangleGeometry(new Rect(-12, Bounds.Height * (1 - reveal), Bounds.Width + 24, Bounds.Height * reveal + 12));
            if (!IsShown && reveal <= 0)
                IsVisible = false;
        }
    }
}
