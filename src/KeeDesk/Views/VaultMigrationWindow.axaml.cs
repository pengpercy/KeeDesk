using Avalonia.Controls;
using Avalonia.Interactivity;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

public partial class VaultMigrationWindow : Window
{
    public VaultMigrationWindow() : this(string.Empty, 0) { }

    internal VaultMigrationWindow(string sourceName, int count)
    {
        InitializeComponent();
        SourceName.Text = sourceName;
        Summary.Text = L("Str_MigrationPreviewFormat", count, sourceName);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Import_Click(object? sender, RoutedEventArgs e) => Close(true);
}
