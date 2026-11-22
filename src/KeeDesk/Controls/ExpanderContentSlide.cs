using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.VisualTree;
using KeeDesk.Helpers;

namespace KeeDesk.Controls;

internal sealed class ExpanderContentSlide : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<ExpanderContentSlide, bool>(nameof(IsExpanded));

    private static readonly StyledProperty<double> RevealProperty =
        AvaloniaProperty.Register<ExpanderContentSlide, double>("Reveal");

    private static readonly SplineEasing ExpandEasing = new() { X1 = 0, Y1 = 0, X2 = 0, Y2 = 1 };
    private static readonly SplineEasing CollapseEasing = new() { X1 = 1, Y1 = 1, X2 = 0, Y2 = 1 };
    private readonly DoubleTransition _slide = new() { Property = RevealProperty };

    static ExpanderContentSlide() => AffectsArrange<ExpanderContentSlide>(RevealProperty);

    public ExpanderContentSlide()
    {
        IsVisible = false;
        ClipToBounds = true;
        Transitions = new Transitions { _slide };
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty)
        {
            if (IsExpanded)
                IsVisible = true;

            _slide.Duration = !this.IsAttachedToVisualTree() || SystemMotionPreferences.ReduceMotion
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(IsExpanded ? 333 : 167);
            _slide.Easing = IsExpanded ? ExpandEasing : CollapseEasing;
            SetValue(RevealProperty, IsExpanded ? 1 : 0);

            if (!IsExpanded && GetValue(RevealProperty) <= 0)
                IsVisible = false;
        }
        else if (change.Property == RevealProperty && !IsExpanded && GetValue(RevealProperty) <= 0)
        {
            IsVisible = false;
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, finalSize.Height * (GetValue(RevealProperty) - 1), finalSize.Width, finalSize.Height));
        return finalSize;
    }
}
