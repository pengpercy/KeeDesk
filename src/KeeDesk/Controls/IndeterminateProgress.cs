using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using KeeDesk.Helpers;

namespace KeeDesk.Controls;

internal sealed class IndeterminateProgress : Control
{
    public static readonly StyledProperty<double> PhaseProperty =
        AvaloniaProperty.Register<IndeterminateProgress, double>(nameof(Phase));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TemplatedControl.ForegroundProperty.AddOwner<IndeterminateProgress>();

    public static readonly DirectProperty<IndeterminateProgress, bool> IsReducedMotionProperty =
        AvaloniaProperty.RegisterDirect<IndeterminateProgress, bool>(nameof(IsReducedMotion), control => control.IsReducedMotion);

    private static readonly LinearGradientBrush SweepMask = new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Colors.Transparent, 0),
            new GradientStop(Colors.White, 0.5),
            new GradientStop(Colors.Transparent, 1)
        ]
    };

    static IndeterminateProgress() => AffectsRender<IndeterminateProgress>(PhaseProperty, ForegroundProperty);

    public bool IsReducedMotion { get; } = SystemMotionPreferences.ReduceMotion;

    public double Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground is not { } foreground || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var width = IsReducedMotion ? Bounds.Width : Bounds.Width / 3;
        var bar = new Rect(IsReducedMotion ? 0 : width * Phase, 0, width, Bounds.Height);
        using var mask = context.PushOpacityMask(SweepMask, bar);
        context.DrawRectangle(foreground, null, bar);
    }
}
