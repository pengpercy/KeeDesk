using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using KeeDesk.Views;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.ViewModels;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _remoteVaultOperation;
    internal Func<CloudDriveRequest, CancellationToken, Task<CloudDriveResult?>>? OpenCloudDriveDialogAsync { get; set; }
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

            var downloaded = await client.DownloadAsync(cancellation.Token);
            opened = await OpenEncryptedWorkingCopyAsync(downloaded.Encrypted, client.Endpoint.AbsolutePath, client, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowToast(DescribeRemoteVaultError(ex), ToastSeverity.Error);
        }
        finally
        {
            _remoteVaultOperation = null;
            if (!opened) client.Dispose();
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task OpenCloudDriveAsync() => RunCloudDriveAsync(upload: false);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task UploadCloudDriveCopyAsync() => RunCloudDriveAsync(upload: true);

    private async Task RunCloudDriveAsync(bool upload)
    {
        if (OpenCloudDriveDialogAsync is null || UnlockRemoteVaultAsync is null || _remoteVaultOperation is not null)
            return;

        string? name = null;
        Func<CancellationToken, Task<byte[]>>? readCopy = null;
        if (upload)
        {
            if (_activeSession is not { } session || session.Database.FilePath is not { } path)
            {
                ShowToast(L("Str_ToSaveChangesToTheDemoCreate"), ToastSeverity.Error);
                return;
            }

            if (!TrySavePendingVault(session)) return;
            var vault = session.Database;
            var baseName = SafeWorkingCopyName(path);
            name = $"{baseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.kdbx";
            readCopy = token =>
            {
                if (_database != vault) throw new OperationCanceledException(token);
                return CloudDriveClient.ReadSavedVaultAsync(vault, token);
            };
        }

        using var cancellation = new CancellationTokenSource();
        _remoteVaultOperation = cancellation;
        try
        {
            var result = await OpenCloudDriveDialogAsync(new CloudDriveRequest(name, readCopy), cancellation.Token);
            if (result is null || cancellation.IsCancellationRequested) return;
            if (result.Encrypted is { } encrypted)
                await OpenEncryptedWorkingCopyAsync(encrypted, result.FileName, null, cancellation.Token);
            else if (upload)
                ShowToast(L("Str_CloudDriveCopyUploadedFormat", result.FileName), ToastSeverity.Success);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowToast(DescribeRemoteVaultError(ex), ToastSeverity.Error);
        }
        finally
        {
            _remoteVaultOperation = null;
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task SynchronizeCloudDriveAsync()
    {
        if (_activeSession is not { } session || _remoteVaultOperation is not null ||
            OpenCloudDriveDialogAsync is null || ShowRemoteSyncAsync is null)
            return;
        var current = session.Database;
        if (current.FilePath is null)
        {
            ShowToast(L("Str_ToSaveChangesToTheDemoCreate"), ToastSeverity.Error);
            return;
        }
        if (!TrySavePendingVault(session)) return;

        using var cancellation = new CancellationTokenSource();
        _remoteVaultOperation = cancellation;
        KdbxDatabase? merged = null;
        try
        {
            var result = await OpenCloudDriveDialogAsync(new CloudDriveRequest(Synchronize: true), cancellation.Token);
            using var client = result?.Client;
            if (client is null || result?.Origin is not { } origin || cancellation.IsCancellationRequested ||
                _activeSession != session || session.Database != current)
                return;

            merged = await ShowRemoteSyncAsync(token =>
                CloudDriveSynchronizationService.SynchronizeAsync(current, session.MasterKey, client, origin, token),
                cancellation);
            ApplyRemoteSyncResult(session, current, ref merged, cancellation.Token, "Str_CloudDriveSynchronized");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (IsRemoteSyncError(ex))
        {
            ShowToast(DescribeRemoteVaultError(ex), ToastSeverity.Error);
        }
        finally
        {
            _remoteVaultOperation = null;
            merged?.Dispose();
        }
    }

    private async Task<bool> OpenEncryptedWorkingCopyAsync(byte[] encrypted, string name, RemoteVaultClient? client,
        CancellationToken cancellation)
    {
        var folder = Path.Combine(SettingsService.RecoveryDirectory, "RemoteVaults");
        Directory.CreateDirectory(folder);
        var safeName = SafeWorkingCopyName(name);
        var localPath = Path.Combine(folder, $"{safeName}-{Guid.NewGuid():N}.kdbx");
        bool created = false;
        bool opened = false;
        try
        {
            await using (var output = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                await output.WriteAsync(encrypted, cancellation);
            }

            var unlocked = await UnlockRemoteVaultAsync!(localPath, cancellation);
            if (unlocked is null) return false;
            var (database, key, keyFile) = unlocked.Value;
            if (cancellation.IsCancellationRequested)
            {
                database.Dispose();
                key.Dispose();
                return false;
            }

            ActivateDatabase(database, key, keyFile);
            if (_activeSession is not { } session || !ReferenceEquals(session.Database, database))
                return false;
            session.RemoteVault = client;
            session.DisplayName = database.Meta.DatabaseName;
            VaultDisplayName = session.DisplayName;
            opened = true;
            SettingsService.AddRecentVault(localPath, keyFile, session.DisplayName);
            RefreshRecentVaults();
            ShowToast(L(client is null ? "Str_CloudDriveOpenedCopy" : "Str_RemoteVaultOpened"), ToastSeverity.Success);
            return true;
        }
        finally
        {
            if (created && !opened)
                try { File.Delete(localPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static string SafeWorkingCopyName(string name)
    {
        var basis = Path.GetFileNameWithoutExtension(name);
        var fileName = string.Concat(basis.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(48));
        return fileName.Length == 0 ? "Vault" : fileName;
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
            return ApplyRemoteSyncResult(session, current, ref merged, cancellation.Token, "Str_RemoteVaultSynchronized");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (IsRemoteSyncError(ex))
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

    private bool ApplyRemoteSyncResult(VaultTabSession session, KdbxDatabase current, ref KdbxDatabase? merged,
        CancellationToken cancellation, string successMessage)
    {
        if (cancellation.IsCancellationRequested || !OpenVaults.Contains(session) ||
            session.Database != current || current.IsModified)
            return false;

        if (merged is not null)
        {
            KdbxFormat.Save(merged, current.FilePath!, session.MasterKey);
            ApplySynchronizedVault(session, merged);
            merged = null;
        }

        ShowToast(L(successMessage), ToastSeverity.Success);
        return true;
    }

    private static bool IsRemoteSyncError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or
            System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or
            ArgumentException or NotSupportedException;

    private static string DescribeRemoteVaultError(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
            => L("Str_RemoteVaultUnauthorized"),
        CloudDriveVaultMismatchException => L("Str_CloudDriveVaultMismatch"),
        CloudDriveRevisionException => L("Str_CloudDriveInvalidRevision"),
        RemoteVaultVersionUnavailableException => L("Str_RemoteVaultNeedsVersion"),
        RemoteVaultConcurrentWriteException or KdbxFileChangedException => L("Str_RemoteVaultChanged"),
        System.Security.Cryptography.CryptographicException or InvalidDataException => L("Str_RemoteVaultKeyMismatch"),
        _ => L("Str_RemoteVaultConnectionFailed")
    };
}
