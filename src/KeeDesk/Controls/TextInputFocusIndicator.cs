using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media.Transformation;
using KeeDesk.Helpers;

namespace KeeDesk.Controls;

internal sealed class TextInputFocusIndicator : Border
{
    public static readonly StyledProperty<bool> IsInputFocusedProperty =
        AvaloniaProperty.Register<TextInputFocusIndicator, bool>(nameof(IsInputFocused));

    private static readonly StyledProperty<bool> IsDropDownOpenProperty =
        AvaloniaProperty.Register<TextInputFocusIndicator, bool>("IsDropDownOpen");

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

    // Keep the Fluent 2 templates and add the underline to their existing border surfaces.
    internal static void AttachToFluent2Templates()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TextBox>((field, args) =>
        {
            if (field.TemplatedParent is CalendarDatePicker or ComboBox) return;
            AddToSurface(field, args.NameScope.Find<Border>("PART_BorderElement"));
        });

        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ButtonSpinner>((spinner, args) =>
        {
            if (spinner.TemplatedParent is NumericUpDown field)
                AddToSurface(field, args.NameScope.Find<Border>("BorderElement"));
        });

        TemplatedControl.TemplateAppliedEvent.AddClassHandler<CalendarDatePicker>((field, args) =>
            AddToSurface(field, args.NameScope.Find<Border>("Background"), CalendarDatePicker.IsDropDownOpenProperty));
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ComboBox>((field, args) =>
            AddToSurface(field, args.NameScope.Find<Border>("Background"), ComboBox.IsDropDownOpenProperty));
    }

    private static void AddToSurface(TemplatedControl field, Border? surface, AvaloniaProperty<bool>? dropDownProperty = null)
    {
        if (surface is not { Child: null }) return;

        var indicator = new TextInputFocusIndicator
        {
            Height = 2,
            Margin = new Thickness(0, 0, 0, -1),
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(0, 0, 2, 2),
            IsHitTestVisible = false
        };
        surface.Child = indicator;

        indicator.Bind(IsInputFocusedProperty, field.GetObservable(IsKeyboardFocusWithinProperty));
        if (dropDownProperty is not null)
            indicator.Bind(IsDropDownOpenProperty, field.GetObservable(dropDownProperty));
    }

    public bool IsInputFocused
    {
        get => GetValue(IsInputFocusedProperty);
        set => SetValue(IsInputFocusedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsInputFocusedProperty && change.Property != IsDropDownOpenProperty) return;

        bool show = IsInputFocused || GetValue(IsDropDownOpenProperty);

        _transition.Duration = SystemMotionPreferences.ReduceMotion
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(show ? 200 : 50);
        RenderTransform = show ? Expanded : Collapsed;
    }
}
