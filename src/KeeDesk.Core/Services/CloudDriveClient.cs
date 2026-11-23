using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeeDesk.Core.Crypto;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record CloudDriveVault(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed class CloudDriveClient : IDisposable
{
    private const int MaxVaultBytes = 64 * 1024 * 1024;
    private static readonly Uri Google = new("https://www.googleapis.com/drive/v3/");
    private static readonly Uri Graph = new("https://graph.microsoft.com/v1.0/");
    private readonly HttpClient _http;
    private readonly ProtectedString _accessToken;
    private readonly string? _pickedGoogleFile;
    private string? _oneDriveFolder;

    internal CloudDriveClient(CloudVaultProvider provider, string accessToken, HttpClient http, string? pickedGoogleFile = null)
    {
        Provider = provider;
        _accessToken = new ProtectedString(true, accessToken);
        _http = http;
        _pickedGoogleFile = pickedGoogleFile;
    }

    public CloudVaultProvider Provider { get; }

    public async Task<IReadOnlyList<CloudDriveVault>> ListAsync(CancellationToken cancellation)
    {
        if (Provider == CloudVaultProvider.GoogleDrive && !string.IsNullOrEmpty(_pickedGoogleFile))
        {
            using var file = await GetJsonAsync(new Uri(Google, $"files/{Id(_pickedGoogleFile)}?fields=id,name,mimeType,trashed&supportsAllDrives=true"), cancellation)
                .ConfigureAwait(false);
            return ParseVault(file.RootElement) is { } vault &&
                !CloudDriveRevision.IsRevisionName(vault.Name) ? [vault] : [];
        }

        var files = await ListFilesAsync(null, cancellation).ConfigureAwait(false);
        return files.Where(file => !CloudDriveRevision.IsRevisionName(file.Name))
            .OrderBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal Task<IReadOnlyList<CloudDriveVault>> ListRevisionsAsync(CloudDriveVault origin, CancellationToken cancellation) =>
        ListFilesAsync(CloudDriveRevision.GetPrefix(Provider, origin.Id), cancellation);

    private async Task<IReadOnlyList<CloudDriveVault>> ListFilesAsync(string? revisionPrefix, CancellationToken cancellation)
    {
        Uri? page = Provider == CloudVaultProvider.GoogleDrive
            ? GooglePage(null, revisionPrefix)
            : new Uri(Graph, $"me/drive/items/{await GetAppFolderAsync(cancellation).ConfigureAwait(false)}/children?$select=id,name,file&$top=100");
        var results = new List<CloudDriveVault>();
        var visited = new HashSet<Uri>();
        while (page is not null)
        {
            if (!visited.Add(page) || visited.Count > 100) throw new InvalidDataException("Invalid cloud folder pagination.");
            using var document = await GetJsonAsync(page, cancellation).ConfigureAwait(false);
            var root = document.RootElement;
            var key = Provider == CloudVaultProvider.GoogleDrive ? "files" : "value";
            if (root.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.ValueKind == JsonValueKind.True ||
                !root.TryGetProperty(key, out var files) || files.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The cloud file listing is incomplete.");

            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind == JsonValueKind.Object &&
                    (Provider == CloudVaultProvider.GoogleDrive || file.TryGetProperty("file", out _)) &&
                    ParseVault(file) is { } vault &&
                    (revisionPrefix is null || vault.Name.StartsWith(revisionPrefix, StringComparison.Ordinal)))
                    results.Add(vault);
            }
            page = Provider == CloudVaultProvider.GoogleDrive
                ? Text(root, "nextPageToken") is { Length: > 0 } token ? GooglePage(token, revisionPrefix) : null
                : Text(root, "@odata.nextLink") is { Length: > 0 } next ? ApiAddress(next, Graph.Host) : null;
        }
        return results;
    }

    public async Task<byte[]> DownloadAsync(CloudDriveVault vault, CancellationToken cancellation)
    {
        string id = Id(vault.Id);
        Uri uri = Provider == CloudVaultProvider.GoogleDrive
            ? new Uri(Google, $"files/{id}?alt=media&supportsAllDrives=true")
            : new Uri(Graph, $"me/drive/items/{id}/content");
        using var request = Authorized(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellation).ConfigureAwait(false);
        if (Provider == CloudVaultProvider.OneDrive && response.StatusCode == HttpStatusCode.Found)
        {
            var target = response.Headers.Location;
            for (int redirect = 0; redirect < 3; redirect++)
            {
                if (!IsOneDriveTransfer(target)) throw new InvalidDataException("Invalid OneDrive download URL.");
                using var read = new HttpRequestMessage(HttpMethod.Get, target);
                using var download = await SendAsync(read, cancellation).ConfigureAwait(false);
                if (download.StatusCode is HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
                {
                    target = download.Headers.Location;
                    continue;
                }
                return await ReadVaultAsync(download, cancellation).ConfigureAwait(false);
            }
            throw new InvalidDataException("Too many OneDrive download redirects.");
        }
        return await ReadVaultAsync(response, cancellation).ConfigureAwait(false);
    }

    public async Task UploadCopyAsync(string name, byte[] encrypted, CancellationToken cancellation)
    {
        if (!name.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase) || name.Length > 128 ||
            name.Any(c => c is '/' or '\\' || char.IsControl(c)))
            throw new ArgumentException("A KDBX file name is required.", nameof(name));
        ValidateVault(encrypted);
        using var content = new ByteArrayContent(encrypted);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (Provider == CloudVaultProvider.GoogleDrive)
        {
            using var start = Authorized(HttpMethod.Post, new Uri("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable"));
            start.Content = new StringContent($"{{\"name\":\"{JsonEncodedText.Encode(name)}\"}}", Encoding.UTF8, "application/json");
            start.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            start.Headers.Add("X-Upload-Content-Length", encrypted.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var session = await SendAsync(start, cancellation).ConfigureAwait(false);
            session.EnsureSuccessStatusCode();
            var target = session.Headers.Location;
            if (!Https(target) || target!.Host != Google.Host || target.AbsolutePath != "/upload/drive/v3/files")
                throw new InvalidDataException("Invalid Google Drive upload URL.");
            using var upload = new HttpRequestMessage(HttpMethod.Put, target) { Content = content };
            using var response = await SendAsync(upload, cancellation).ConfigureAwait(false);
            ConfirmUpload(response);
            return;
        }

        var folder = await GetAppFolderAsync(cancellation).ConfigureAwait(false);
        var escaped = Uri.EscapeDataString(name);
        using var request = Authorized(HttpMethod.Put, new Uri(Graph,
            $"me/drive/items/{folder}:/{escaped}:/content?%40microsoft.graph.conflictBehavior=fail"));
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        request.Content = content;
        using var created = await SendAsync(request, cancellation).ConfigureAwait(false);
        ConfirmUpload(created);
    }

    public static async Task<byte[]> ReadSavedVaultAsync(KdbxDatabase vault, CancellationToken cancellation)
    {
        if (vault.FilePath is not { } path || vault.SavedFileHash is not { } hash)
            throw new ArgumentException("Save the vault before uploading a copy.");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = await ReadLocalVaultAsync(stream, cancellation).ConfigureAwait(false);
        if (!hash.AsSpan().SequenceEqual(SHA256.HashData(bytes))) throw new KdbxFileChangedException();
        return bytes;
    }

    private static async Task<byte[]> ReadLocalVaultAsync(Stream stream, CancellationToken cancellation)
    {
        using var data = new MemoryStream();
        await CopyBoundedAsync(stream, data, MaxVaultBytes, cancellation).ConfigureAwait(false);
        var encrypted = data.ToArray();
        ValidateVault(encrypted);
        return encrypted;
    }

    private async Task<string> GetAppFolderAsync(CancellationToken cancellation)
    {
        if (_oneDriveFolder is not null) return _oneDriveFolder;
        using var folder = await GetJsonAsync(new Uri(Graph, "me/drive/special/approot?$select=id"), cancellation)
            .ConfigureAwait(false);
        return _oneDriveFolder = Id(Text(folder.RootElement, "id"));
    }

    private static Uri GooglePage(string? token, string? prefix)
    {
        var query = "trashed = false" + (prefix is null ? "" : $" and name contains '{prefix}'");
        return new Uri(Google, $"files?q={Uri.EscapeDataString(query)}&fields=nextPageToken,incompleteSearch,files(id,name,mimeType)&pageSize=100" +
            (token is null ? "" : $"&pageToken={Uri.EscapeDataString(token)}"));
    }

    private static CloudDriveVault? ParseVault(JsonElement file)
    {
        string name = Text(file, "name");
        string id = Text(file, "id");
        return id.Length > 0 && name.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase) &&
            !Text(file, "mimeType").StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) &&
            !(file.TryGetProperty("trashed", out var trash) && trash.ValueKind == JsonValueKind.True)
            ? new CloudDriveVault(id, name) : null;
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellation)
    {
        using var request = Authorized(HttpMethod.Get, uri);
        using var response = await SendAsync(request, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync(response, cancellation).ConfigureAwait(false);
    }

    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var data = new MemoryStream();
        try
        {
            await CopyBoundedAsync(stream, data, 2 * 1024 * 1024, cancellation).ConfigureAwait(false);
            data.Position = 0;
            return await JsonDocument.ParseAsync(data, cancellationToken: cancellation).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(data.GetBuffer()); }
    }

    private static async Task<byte[]> ReadVaultAsync(HttpResponseMessage response, CancellationToken cancellation)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxVaultBytes)
            throw new InvalidDataException("The cloud service returned an incomplete or oversized vault.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        return await ReadLocalVaultAsync(stream, cancellation).ConfigureAwait(false);
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, int maxBytes, CancellationToken cancellation)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
            {
                if (destination.Length + read > maxBytes) throw new InvalidDataException("The cloud response exceeds its size limit.");
                destination.Write(buffer, 0, read);
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static void ValidateVault(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12 || bytes.Length > MaxVaultBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != KdbxConstants.Signature1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != KdbxConstants.Signature2)
            throw new InvalidDataException("Expected an encrypted KDBX vault of at most 64 MiB.");
    }

    private static void ConfirmUpload(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
            throw new InvalidDataException("The cloud service did not confirm the new copy.");
    }

    private HttpRequestMessage Authorized(HttpMethod method, Uri uri)
    {
        if (!Https(uri) || uri.Host != (Provider == CloudVaultProvider.GoogleDrive ? Google.Host : Graph.Host))
            throw new InvalidDataException("Invalid cloud API URL.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken.ReadString());
        return request;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation) =>
        _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);

    private static string Text(JsonElement owner, string key) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Id(string value)
    {
        if (value.Length == 0 || value is "." or ".." || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '!' or '.'))
            throw new InvalidDataException("Invalid cloud item ID.");
        return Uri.EscapeDataString(value);
    }

    private static bool Https(Uri? uri) => RemoteVaultClient.IsSupportedEndpoint(uri) && uri!.IsDefaultPort;
    private static Uri ApiAddress(string value, string host) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && Https(uri) && uri.Host == host
            ? uri : throw new InvalidDataException("Invalid cloud pagination URL.");

    private static bool IsOneDriveTransfer(Uri? uri) => Https(uri) &&
        (uri!.Host.EndsWith(".1drv.com", StringComparison.Ordinal) ||
         uri.Host.EndsWith(".onedrive.com", StringComparison.Ordinal) ||
         uri.Host.EndsWith(".storage.live.com", StringComparison.Ordinal));

    public void Dispose() => _accessToken.Dispose();
}
