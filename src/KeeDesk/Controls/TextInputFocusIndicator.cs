using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using KeeDesk.Helpers;

namespace KeeDesk.Controls;

internal sealed class TextInputFocusIndicator : Border
{
    public static readonly StyledProperty<bool> IsInputFocusedProperty =
        AvaloniaProperty.Register<TextInputFocusIndicator, bool>(nameof(IsInputFocused));

    private static readonly TransformOperations Collapsed = TransformOperations.Parse("scaleX(0)");
    private static readonly TransformOperations Expanded = TransformOperations.Parse("scaleX(1)");

    private readonly TransformOperationsTransition _transition = new()
    {
        Property = RenderTransformProperty,
        // Fluent UI Input leaves transition-timing-function at the CSS default (ease).
        Easing = new SplineEasing { X1 = 0.25, Y1 = 0.1, X2 = 0.25, Y2 = 1 }
    };

    public TextInputFocusIndicator()
    {
        RenderTransform = Collapsed;
        Transitions = new Transitions { _transition };
    }

    public bool IsInputFocused
    {
        get => GetValue(IsInputFocusedProperty);
        set => SetValue(IsInputFocusedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsInputFocusedProperty) return;

        _transition.Duration = SystemMotionPreferences.ReduceMotion
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(IsInputFocused ? 200 : 50);
        RenderTransform = IsInputFocused ? Expanded : Collapsed;
    }
}
