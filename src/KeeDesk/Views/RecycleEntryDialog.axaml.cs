using Avalonia.Controls;
using Avalonia.Interactivity;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

public partial class RecycleEntryDialog : Window
{
    public RecycleEntryDialog() : this(string.Empty) { }

    public RecycleEntryDialog(string title)
    {
        InitializeComponent();
        Prompt.Text = L("Str_MoveToTheRecycleBinFormat", title);
        Opened += (_, _) => CancelButton.Focus();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
}
