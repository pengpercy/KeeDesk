using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeeDesk.Helpers;

namespace KeeDesk.Views;

/// <summary>
/// 新建或重命名分组的轻量级 Fluent 2 对话框。
/// </summary>
public sealed record GroupDialogResult(string Name, string Notes);

public partial class GroupDialogWindow : Window
{
    public GroupDialogWindow() : this("新建分组", string.Empty, string.Empty) { }

    public GroupDialogWindow(string heading, string initialName, string initialNotes)
    {
        InitializeComponent();
        Title = heading;
        DialogHeading.Text = heading;
        NameBox.Text = initialName;
        NotesBox.Text = initialNotes;

        MacTitleBarInsets.AttachTrafficLightsCentering(this, 40);
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e)
    {
        var name = (NameBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorText.Text = "请输入分组名称。";
            ErrorText.IsVisible = true;
            NameBox.Focus();
            return;
        }
        Close(new GroupDialogResult(name, (NotesBox.Text ?? string.Empty).Trim()));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm_Click(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 1 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var src = e.Source as Avalonia.Visual;
        while (src is not null)
        {
            if (src is Button or TextBox) return;
            src = src.GetVisualParent();
        }
        BeginMoveDrag(e);
    }
}
