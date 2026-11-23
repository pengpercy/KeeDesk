using Avalonia.Controls;
using Avalonia.Interactivity;
using KeeDesk.Core.Services;
using KeeDesk.Services;

namespace KeeDesk.Views;

public partial class VaultPrintWindow : Window
{
    public VaultPrintWindow() => InitializeComponent();

    private void Print_Click(object? sender, RoutedEventArgs e) =>
        Close(new VaultPrintOptions(LayoutBox.SelectedIndex == 1, IncludePasswordsCheck.IsChecked == true,
            SelectionCheck.IsChecked == true, LocalizationService.CurrentLanguage));

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
