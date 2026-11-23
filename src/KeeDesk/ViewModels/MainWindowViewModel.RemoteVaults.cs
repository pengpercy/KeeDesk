using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _remoteVaultOperation;
    public Func<Uri?, Task<RemoteVaultClient?>>? PromptRemoteVaultAsync { get; set; }
    public Func<Func<CancellationToken, Task<KdbxDatabase?>>, CancellationTokenSource, Task<KdbxDatabase?>>? ShowRemoteSyncAsync { get; set; }
    public Func<string, CancellationToken, Task<(KdbxDatabase Database, CompositeKey Key, string KeyFile)?>>? UnlockRemoteVaultAsync { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task OpenRemoteVaultAsync()
    {
        if (PromptRemoteVaultAsync is null || UnlockRemoteVaultAsync is null || _remoteVaultOperation is not null)
            return;

        var client = await PromptRemoteVaultAsync(null);
        if (client is null) return;
        string? localPath = null;
        bool opened = false;
        using var cancellation = new CancellationTokenSource();
        _remoteVaultOperation = cancellation;
        try
        {
            var alreadyOpen = OpenVaults.FirstOrDefault(session => session.RemoteVault?.Endpoint == client.Endpoint);
            if (alreadyOpen is not null)
            {
                SwitchVaultTab(alreadyOpen);
                return;
            }
            // Only the encrypted bytes are placed in this local working copy.
            var downloaded = await client.DownloadAsync(cancellation.Token);
            var folder = Path.Combine(SettingsService.RecoveryDirectory, "RemoteVaults");
            Directory.CreateDirectory(folder);
            var name = Path.GetFileNameWithoutExtension(client.Endpoint.AbsolutePath);
            var safeName = string.Concat(name.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').Take(60));
            if (safeName.Length == 0) safeName = "Vault";
            localPath = Path.Combine(folder, $"{safeName}-{Guid.NewGuid():N}.kdbx");
            await using (var output = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(downloaded.Encrypted, cancellation.Token);

            var unlocked = await UnlockRemoteVaultAsync(localPath, cancellation.Token);
            if (unlocked is null) return;
            var (database, key, keyFile) = unlocked.Value;
            if (cancellation.IsCancellationRequested)
            {
                database.Dispose();
                key.Dispose();
                return;
            }

            ActivateDatabase(database, key, keyFile);
            if (_activeSession is not { } session || !ReferenceEquals(session.Database, database))
                return;
            session.RemoteVault = client;
            session.DisplayName = database.Meta.DatabaseName;
            VaultDisplayName = session.DisplayName;
            opened = true;
            SettingsService.AddRecentVault(localPath, keyFile, session.DisplayName);
            RefreshRecentVaults();
            ShowToast(L("Str_RemoteVaultOpened"), ToastSeverity.Success);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowToast(DescribeRemoteVaultError(ex), ToastSeverity.Error);
        }
        finally
        {
            _remoteVaultOperation = null;
            if (!opened)
            {
                client.Dispose();
                if (localPath is not null)
                    try { File.Delete(localPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
            }
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task SynchronizeRemoteVaultAsync()
    {
        var session = _activeSession;
        if (session is null || PromptRemoteVaultAsync is null || _remoteVaultOperation is not null) return;
        if (session.Database.FilePath is null)
        {
            ShowToast(L("Str_ToSaveChangesToTheDemoCreate"), ToastSeverity.Error);
            return;
        }

        var client = session.RemoteVault ?? await PromptRemoteVaultAsync(null);
        if (client is null) return;
        bool pendingConnection = session.RemoteVault != client;
        try
        {
            if (session != _activeSession) return;
            if (await SynchronizeRemoteVaultAsync(session, client) && pendingConnection && OpenVaults.Contains(session))
            {
                session.RemoteVault = client;
                pendingConnection = false;
            }
        }
        finally
        {
            if (pendingConnection) client.Dispose();
        }
    }

    private async Task<bool> SynchronizeRemoteVaultAsync(VaultTabSession session, RemoteVaultClient client)
    {
        if (_remoteVaultOperation is not null || ShowRemoteSyncAsync is null || !TrySavePendingVault(session)) return false;

        var current = session.Database;
        using var cancellation = new CancellationTokenSource();
        _remoteVaultOperation = cancellation;
        KdbxDatabase? merged = null;
        try
        {
            merged = await ShowRemoteSyncAsync(token =>
                RemoteVaultSynchronizationService.SynchronizeAsync(current, session.MasterKey, client, token), cancellation);
            if (cancellation.IsCancellationRequested || !OpenVaults.Contains(session) ||
                !ReferenceEquals(session.Database, current) || current.IsModified) return false;

            if (merged is not null)
            {
                KdbxFormat.Save(merged, current.FilePath!, session.MasterKey);
                ApplySynchronizedVault(session, merged);
                merged = null;
            }

            ShowToast(L("Str_RemoteVaultSynchronized"), ToastSeverity.Success);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or
            System.Security.Cryptography.CryptographicException or ArgumentException or NotSupportedException)
        {
            ShowToast(DescribeRemoteVaultError(ex), ToastSeverity.Error);
        }
        finally
        {
            _remoteVaultOperation = null;
            merged?.Dispose();
        }
        return false;
    }

    private static string DescribeRemoteVaultError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
            => L("Str_RemoteVaultUnauthorized"),
        RemoteVaultVersionUnavailableException => L("Str_RemoteVaultNeedsVersion"),
        RemoteVaultConcurrentWriteException or KdbxFileChangedException => L("Str_RemoteVaultChanged"),
        System.Security.Cryptography.CryptographicException or InvalidDataException => L("Str_RemoteVaultKeyMismatch"),
        _ => L("Str_RemoteVaultConnectionFailed")
    };
}
