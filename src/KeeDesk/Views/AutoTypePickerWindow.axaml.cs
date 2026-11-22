using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeeDesk.Core.Services;
using KeeDesk.Helpers;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

/// <summary>
/// 对标原版 KeePass 2.x AutoTypeCtxForm：当全局热键匹配到多个候选条目时供用户快速回车确认。
/// </summary>
public partial class AutoTypePickerWindow : Window
{
    private readonly List<AutoTypeMatchResult> _matches;

    public AutoTypePickerWindow() : this(string.Empty, new List<AutoTypeMatchResult>()) { }

    public AutoTypePickerWindow(string targetWindowTitle, List<AutoTypeMatchResult> matches)
    {
        InitializeComponent();
        _matches = matches;
        TargetWindowLabel.Text = string.IsNullOrWhiteSpace(targetWindowTitle)
            ? L("Str_ChooseAnEntryForAutoType")
            : L("Str_TargetWindowFormat", targetWindowTitle);

        MacTitleBarInsets.AttachTrafficLightsCentering(this, 40);

        MatchListBox.ItemsSource = _matches
            .Select(m => $"{m.Entry.Title}  —  {m.Entry.UserName}  [{DescribeMatch(m)}]")
            .ToList();
        if (_matches.Count > 0) MatchListBox.SelectedIndex = 0;

        Opened += (_, _) => MatchListBox.Focus();
    }

    private static string DescribeMatch(AutoTypeMatchResult match) => match.Kind switch
    {
        AutoTypeMatchKind.Rule => L("Str_RuleMatchFormat", match.MatchValue),
        AutoTypeMatchKind.Title => L("Str_WindowTitleMatch"),
        AutoTypeMatchKind.Domain => L("Str_DomainMatchFormat", match.MatchValue),
        _ => match.MatchReason
    };

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        if (MatchListBox.SelectedIndex >= 0 && MatchListBox.SelectedIndex < _matches.Count)
            Close(_matches[MatchListBox.SelectedIndex]);
        else
            Close(null);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void MatchListBox_DoubleTapped(object? sender, TappedEventArgs e) => Confirm_Click(sender, e);

    private void MatchListBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Confirm_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or ListBox) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
