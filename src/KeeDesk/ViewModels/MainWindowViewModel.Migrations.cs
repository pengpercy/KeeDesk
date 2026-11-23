using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.ViewModels;

public sealed partial class MainWindowViewModel
{
    public Func<string, int, Task<bool>>? ConfirmVaultMigrationAsync { get; set; }

    [RelayCommand]
    private async Task ImportVaultMigrationAsync()
    {
        if (CurrentDatabase is not { } vault || PickOpenFileAsync is null || ConfirmVaultMigrationAsync is null)
            return;
        var path = await PickOpenFileAsync("migration");
        if (string.IsNullOrWhiteSpace(path) || CurrentDatabase != vault) return;

        var extension = Path.GetExtension(path);
        bool bitwarden = extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
        if (!bitwarden && !extension.Equals(".1pux", StringComparison.OrdinalIgnoreCase))
        {
            ShowToast(L("Str_MigrationInvalid"), ToastSeverity.Error);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        void CancelOnVaultChange(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CurrentDatabase) && CurrentDatabase != vault)
                cancellation.Cancel();
        }

        PropertyChanged += CancelOnVaultChange;
        KdbxGroup? group = null;
        try
        {
            group = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return bitwarden ? VaultMigrationReader.ReadBitwarden(stream, cancellation.Token)
                    : VaultMigrationReader.ReadOnePassword(stream, cancellation.Token);
            }, cancellation.Token);
            if (CurrentDatabase != vault || cancellation.IsCancellationRequested) return;
            int count = EnumerateGroupEntries(group).Count();
            if (count == 0)
            {
                ShowToast(L("Str_MigrationEmpty"));
                return;
            }
            if (!await ConfirmVaultMigrationAsync(group.Name, count) || CurrentDatabase != vault) return;
            vault.RootGroup.AddGroup(group);
            group = null;
            vault.IsModified = true;
            bool saved = TrySaveActiveVault();
            RebuildWorkspace();
            if (saved) ShowToast(L("Str_MigrationImportedFormat", count), ToastSeverity.Success);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (EncryptedVaultMigrationException)
        {
            if (CurrentDatabase == vault) ShowToast(L("Str_MigrationEncryptedBitwarden"), ToastSeverity.Error);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
        {
            if (CurrentDatabase == vault) ShowToast(L("Str_MigrationInvalid"), ToastSeverity.Error);
        }
        finally
        {
            PropertyChanged -= CancelOnVaultChange;
            group?.Dispose();
        }
    }
}
