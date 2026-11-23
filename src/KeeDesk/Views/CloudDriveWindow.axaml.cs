using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;
using static KeeDesk.Services.LocalizationService;

namespace KeeDesk.Views;

internal sealed record CloudDriveRequest(string? CopyName = null, Func<CancellationToken, Task<byte[]>>? ReadCopy = null,
    bool Synchronize = false);

internal sealed record CloudDriveResult(string FileName, byte[]? Encrypted = null,
    CloudDriveVault? Origin = null, CloudDriveClient? Client = null);

public partial class CloudDriveWindow : Window
{
    private readonly CloudDriveRequest _request;
    private readonly CancellationToken _vaultCancellation;
    private CancellationTokenSource? _operation;
    private CloudDriveClient? _client;
    private bool _ready;
    private bool _closed;

    public CloudDriveWindow() : this(new CloudDriveRequest(), CancellationToken.None) { }

    internal CloudDriveWindow(CloudDriveRequest request, CancellationToken cancellation)
    {
        _request = request;
        _vaultCancellation = cancellation;
        InitializeComponent();
        if (request.Synchronize)
        {
            Heading.Text = Title = L("Str_CloudDriveSync");
            LocalCopyHint.Text = L("Str_CloudDriveSyncHint");
            OpenButton.Content = L("Str_CloudDriveSyncConfirm");
        }
        else if (request.CopyName is not null)
            Heading.Text = Title = L("Str_CloudDriveUpload");
        _ready = true;
        ProviderBox.SelectedIndex = 0;
        Closed += (_, _) =>
        {
            _closed = true;
            _operation?.Cancel();
            ClientSecretBox.Text = string.Empty;
            if (_operation is null) _client?.Dispose();
        };
    }

    private CloudVaultProvider SelectedProvider =>
        ProviderBox.SelectedIndex == 1 ? CloudVaultProvider.OneDrive : CloudVaultProvider.GoogleDrive;

    private void Provider_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        bool google = SelectedProvider == CloudVaultProvider.GoogleDrive;
        var idVariable = google ? "KEEDESK_GOOGLE_CLIENT_ID" : "KEEDESK_ONEDRIVE_CLIENT_ID";
        ClientIdBox.Text = Environment.GetEnvironmentVariable(idVariable);
        ClientSecretBox.IsVisible = google;
        ClientSecretBox.Text = google ? Environment.GetEnvironmentVariable("KEEDESK_GOOGLE_CLIENT_SECRET") : null;
        ProviderHint.Text = L(google ? "Str_CloudDriveGoogleHint" : "Str_CloudDriveOneDriveHint");
        Error.IsVisible = false;
        ConnectButton.IsEnabled = !string.IsNullOrWhiteSpace(ClientIdBox.Text);
    }

    private void Credential_Changed(object? sender, TextChangedEventArgs e)
    {
        if (_ready) ConnectButton.IsEnabled = _operation is null && !string.IsNullOrWhiteSpace(ClientIdBox.Text);
    }

    private void Vault_Changed(object? sender, SelectionChangedEventArgs e) =>
        OpenButton.IsEnabled = VaultList.SelectedItem is CloudDriveVault;

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (ClientIdBox.Text?.Trim() is not { Length: > 0 } clientId) return;
        await RunAsync("Str_CloudDriveWaiting", async token =>
        {
            var provider = SelectedProvider;
            _client?.Dispose();
            _client = await CloudDriveAuthorization.ConnectAsync(provider, clientId, ClientSecretBox.Text?.Trim(),
                pickFile: _request.ReadCopy is null, url => Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }),
                token);
            if (_closed || _client is null) return;
            ClientSecretBox.Text = string.Empty;
            if (_request.ReadCopy is { } readCopy && _request.CopyName is { } name)
            {
                Status.Text = L("Str_CloudDriveUploading");
                var bytes = await readCopy(token);
                token.ThrowIfCancellationRequested();
                await _client.UploadCopyAsync(name, bytes, token);
                if (!_closed && !token.IsCancellationRequested) Close(new CloudDriveResult(name));
                return;
            }

            Status.Text = L("Str_CloudDriveLoading");
            var files = await _client.ListAsync(token);
            if (_closed || token.IsCancellationRequested) return;
            if (files.Count == 0)
            {
                Error.Text = L("Str_CloudDriveNoVaults");
                Error.IsVisible = true;
                return;
            }
            ConnectionFields.IsVisible = ConnectButton.IsVisible = false;
            VaultList.ItemsSource = files;
            VaultList.IsVisible = LocalCopyHint.IsVisible = OpenButton.IsVisible = true;
            VaultList.SelectedIndex = 0;
            OpenButton.Focus();
        });
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (_operation is not null || _client is not { } client || VaultList.SelectedItem is not CloudDriveVault file)
            return;
        if (_request.Synchronize)
        {
            _client = null;
            Close(new CloudDriveResult(file.Name, Origin: file, Client: client));
            return;
        }
        await RunAsync("Str_CloudDriveDownloading", async token =>
        {
            var bytes = await client.DownloadAsync(file, token);
            if (!_closed && !token.IsCancellationRequested) Close(new CloudDriveResult(file.Name, bytes));
        });
    }

    private async Task RunAsync(string statusKey, Func<CancellationToken, Task> action)
    {
        if (_operation is not null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_vaultCancellation);
        operation.CancelAfter(TimeSpan.FromMinutes(6));
        _operation = operation;
        Error.IsVisible = false;
        SetBusy(true, statusKey);
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (_closed || _vaultCancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or SocketException or JsonException
            or UnauthorizedAccessException or OperationCanceledException or Win32Exception or InvalidOperationException)
        {
            if (_closed) return;
            Error.Text = L(ex switch
            {
                KdbxFileChangedException => "Str_VaultChangedOnDisk",
                InvalidDataException => "Str_CloudDriveInvalidData",
                _ => "Str_CloudDriveConnectionFailed"
            });
            Error.IsVisible = true;
        }
        finally
        {
            _operation = null;
            if (_closed) _client?.Dispose();
            else SetBusy(false, statusKey);
        }
    }

    private void SetBusy(bool busy, string statusKey)
    {
        Status.Text = L(statusKey);
        Status.IsVisible = BusyIndicator.IsVisible = busy;
        ConnectionFields.IsEnabled = VaultList.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(ClientIdBox.Text);
        OpenButton.IsEnabled = !busy && VaultList.SelectedItem is CloudDriveVault;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
