using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using KeeDesk.Helpers;

namespace KeeDesk.Views;

public partial class MainWindow
{
    private static readonly Size WelcomeSize = new(1080, 720);
    private bool _workspaceReady;
    private bool _enteringWorkspace;
    private int _presentationVersion;

    private readonly record struct WindowAnchor(PixelPoint Center, PixelRect WorkArea, double Scaling, Size FramePadding);

    private void PresentWelcome()
    {
        CaptureNormalWindowSize();
        var anchor = GetWindowAnchor();
        ++_presentationVersion;
        _workspaceReady = false;
        _enteringWorkspace = false;
        WorkspaceStage.IsVisible = false;
        SetWorkspaceChrome(false);
        UnlockBadge.Opacity = 1;
        UnlockStage.IsVisible = true;
        UnlockStage.IsHitTestVisible = true;
        UnlockCurtain.IsVisible = false;
        if (WindowState is WindowState.Maximized or WindowState.FullScreen)
            WindowState = WindowState.Normal;
        CanResize = false;
        SetPresentationSize(WelcomeSize, anchor);
    }

    private void PresentWorkspaceImmediately()
    {
        ++_presentationVersion;
        SetPresentationSize(GetWorkspaceSize(), GetWindowAnchor());
        CompleteWorkspacePresentation();
    }

    private void BeginWorkspaceTransition()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || SystemMotionPreferences.ReduceMotion)
        {
            PresentWorkspaceImmediately();
            return;
        }

        const double coverDuration = 140;
        const double revealDuration = 230;
        var startSize = ClientSize;
        var targetSize = GetWorkspaceSize();
        var anchor = GetWindowAnchor();
        double revealStart = startSize == targetSize ? 205 : 345;
        var version = ++_presentationVersion;
        _enteringWorkspace = true;

        // Match the form emblem before it moves, so the first rendered frame is continuous.
        var center = PresentationRoot.Bounds.Center;
        var badgeSize = UnlockBadge.Bounds.Size;
        var badgeCenter = UnlockBadge.TranslatePoint(new Point(badgeSize.Width / 2, badgeSize.Height / 2), PresentationRoot) ?? center;
        var travel = badgeCenter - center;
        var emblemPosition = new TranslateTransform(travel.X, travel.Y);
        var tileScale = new ScaleTransform(1, 1);
        var haloScale = new ScaleTransform(0.45, 0.45);
        var ringScale = new ScaleTransform(0.60, 0.60);
        var sidebarSlide = new TranslateTransform(-12, 0);
        var listSlide = new TranslateTransform(0, 12);
        var detailSlide = new TranslateTransform(0, 20);
        UnlockEmblem.RenderTransform = emblemPosition;
        UnlockEmblemTile.RenderTransform = tileScale;
        UnlockHalo.RenderTransform = haloScale;
        UnlockRing.RenderTransform = ringScale;
        UnlockVeil.Opacity = 0;
        UnlockEmblem.Opacity = 1;
        UnlockHalo.Opacity = 0;
        UnlockRing.Opacity = 0;
        UnlockClosedGlyph.Opacity = 1;
        UnlockOpenGlyph.Opacity = 0;
        UnlockBadge.Opacity = 0;
        UnlockStage.IsHitTestVisible = false;
        UnlockCurtain.IsVisible = true;
        UnlockCurtain.Focus();

        TimeSpan? startedAt = null;
        TimeSpan? revealStartedAt = null;
        RequestAnimationFrame(Advance);

        void Advance(TimeSpan timestamp)
        {
            if (version != _presentationVersion) return;
            if (WindowState == WindowState.Minimized)
            {
                SetPresentationSize(targetSize, anchor);
                CompleteWorkspacePresentation();
                return;
            }

            if (WorkspaceStage.IsVisible)
            {
                // The first workspace layout runs underneath the veil before the reveal clock starts.
                var revealTime = (timestamp - (revealStartedAt ??= timestamp)).TotalMilliseconds;
                if (revealTime >= revealDuration)
                {
                    CompleteWorkspacePresentation();
                    return;
                }

                UnlockVeil.Opacity = 1 - Ease(revealTime, 0, revealDuration);
                UnlockEmblem.Opacity = 1 - Ease(revealTime, 0, 108);
                sidebarSlide.X = -12 * (1 - Ease(revealTime, 0, 175));
                listSlide.Y = 12 * (1 - Ease(revealTime, 18, 205));
                detailSlide.Y = 20 * (1 - Ease(revealTime, 42, revealDuration));
                RequestAnimationFrame(Advance);
                return;
            }

            var elapsed = (timestamp - (startedAt ??= timestamp)).TotalMilliseconds;
            UnlockVeil.Opacity = Ease(elapsed, 0, coverDuration);
            var move = Ease(elapsed, 0, revealStart);
            emblemPosition.X = travel.X * (1 - move);
            emblemPosition.Y = travel.Y * (1 - move);
            var unlock = Ease(elapsed, 65, 165);
            UnlockClosedGlyph.Opacity = 1 - unlock;
            UnlockOpenGlyph.Opacity = unlock;
            var pulse = Ease(elapsed, 65, revealStart + 65);
            tileScale.ScaleX = tileScale.ScaleY = 1 + 0.14 * Math.Sin(Math.PI * pulse);
            haloScale.ScaleX = haloScale.ScaleY = 0.45 + 0.67 * pulse;
            ringScale.ScaleX = ringScale.ScaleY = 0.60 + 0.86 * pulse;
            UnlockHalo.Opacity = 0.65 * Math.Sin(Math.PI * pulse);
            UnlockRing.Opacity = 0.28 * Math.Sin(Math.PI * pulse);

            if (elapsed >= coverDuration)
            {
                UnlockStage.IsVisible = false;
                if (startSize != targetSize)
                {
                    var growth = Ease(elapsed, coverDuration, revealStart);
                    var size = new Size(
                        Math.Round(startSize.Width + (targetSize.Width - startSize.Width) * growth),
                        Math.Round(startSize.Height + (targetSize.Height - startSize.Height) * growth));
                    SetPresentationSize(size, anchor);
                }
                if (elapsed >= revealStart)
                {
                    SetPresentationSize(targetSize, anchor);
                    WorkspaceSidebar.RenderTransform = sidebarSlide;
                    EntryPane.RenderTransform = listSlide;
                    EntryDetail.RenderTransform = detailSlide;
                    WorkspaceStage.IsHitTestVisible = false;
                    WorkspaceStage.IsVisible = true;
                    SetWorkspaceChrome(true);
                    UpdateWorkspaceLayout();
                }
            }

            RequestAnimationFrame(Advance);
        }
    }

    private void CompleteWorkspacePresentation()
    {
        var restoreFocus = _enteringWorkspace;
        UnlockStage.IsVisible = false;
        WorkspaceSidebar.RenderTransform = null;
        EntryPane.RenderTransform = null;
        EntryDetail.RenderTransform = null;
        WorkspaceStage.IsVisible = true;
        WorkspaceStage.IsHitTestVisible = true;
        SetWorkspaceChrome(true);
        UnlockCurtain.IsVisible = false;
        _enteringWorkspace = false;
        _workspaceReady = true;
        CanResize = true;
        UpdateWorkspaceLayout();
        if (restoreFocus && IsActive && EntryListBox.IsEffectivelyVisible)
            EntryListBox.Focus();
    }

    private void SetWorkspaceChrome(bool visible)
    {
        DesktopWelcomeTitle.IsVisible = !visible;
        DesktopVaultPanel.IsVisible = visible;
        DesktopSearchSlot.IsVisible = visible;
        DesktopCommands.IsVisible = visible;
    }

    private WindowAnchor? GetWindowAnchor()
    {
        if (!IsVisible || WindowState != WindowState.Normal || Screens.ScreenFromWindow(this) is not { Scaling: > 0 } screen
            || screen.WorkingArea.Width <= 0 || screen.WorkingArea.Height <= 0)
            return null;
        var frame = FrameSize ?? ClientSize;
        var center = new PixelPoint(Position.X + (int)Math.Round(frame.Width * screen.Scaling / 2),
                                    Position.Y + (int)Math.Round(frame.Height * screen.Scaling / 2));
        return new WindowAnchor(center, screen.WorkingArea, screen.Scaling, frame - ClientSize);
    }

    private void SetPresentationSize(Size size, WindowAnchor? anchor)
    {
        if (Width == size.Width && Height == size.Height) return;
        Width = size.Width;
        Height = size.Height;
        if (anchor is not { } placement || WindowState != WindowState.Normal) return;
        var area = placement.WorkArea;
        var frameWidth = (int)Math.Round((size.Width + placement.FramePadding.Width) * placement.Scaling);
        var frameHeight = (int)Math.Round((size.Height + placement.FramePadding.Height) * placement.Scaling);
        var left = Math.Clamp(placement.Center.X - frameWidth / 2, area.X, Math.Max(area.X, area.Right - frameWidth));
        var top = Math.Clamp(placement.Center.Y - frameHeight / 2, area.Y, Math.Max(area.Y, area.Bottom - frameHeight));
        Position = new PixelPoint(left, top);
    }

    private static double Ease(double time, double start, double end)
    {
        var t = Math.Clamp((time - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
