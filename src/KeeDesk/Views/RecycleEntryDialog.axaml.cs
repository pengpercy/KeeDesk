using Avalonia.Controls;
using Avalonia.Interactivity;

namespace KeeDesk.Views;

public partial class RecycleEntryDialog : Window
{
    public RecycleEntryDialog() : this(string.Empty) { }

    public RecycleEntryDialog(string title)
    {
        InitializeComponent();
        Prompt.Text = $"将 {title} 移到回收站？";
        Opened += (_, _) => CancelButton.Focus();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
}
