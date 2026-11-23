using Avalonia.Controls;
using Avalonia.Interactivity;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

internal sealed record CloudVaultSelection(string? DirectoryPath);

public partial class CloudVaultWindow : Window
{
    private readonly IReadOnlyList<CloudVaultLocation> _locations;
    private bool _ready;

    public CloudVaultWindow() : this([], false) { }

    internal CloudVaultWindow(IReadOnlyList<CloudVaultLocation> locations, bool synchronize)
    {
        _locations = locations;
        InitializeComponent();
        if (synchronize)
            Title = Heading.Text = L("Str_SyncCloudVault");
        int initial = locations.Any(location => location.Provider == CloudVaultProvider.GoogleDrive) ? 0
            : locations.Any(location => location.Provider == CloudVaultProvider.OneDrive) ? 1 : 0;
        ProviderBox.SelectedIndex = initial;
        _ready = true;
        RefreshFolders();
    }

    private void Provider_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_ready) RefreshFolders();
    }

    private void RefreshFolders()
    {
        var provider = (CloudVaultProvider)Math.Max(0, ProviderBox.SelectedIndex);
        var folders = _locations.Where(location => location.Provider == provider)
            .Select(location => location.DirectoryPath).ToArray();
        FolderBox.ItemsSource = folders;
        FolderBox.SelectedIndex = folders.Length > 0 ? 0 : -1;
        FolderBox.IsVisible = folders.Length > 0;
        FolderHint.Text = L(folders.Length > 0 || provider == CloudVaultProvider.Other
            ? "Str_CloudVaultClientManaged" : "Str_CloudVaultNoFolder");
    }

    private void Choose_Click(object? sender, RoutedEventArgs e) => Close(new CloudVaultSelection(FolderBox.SelectedItem as string));

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
