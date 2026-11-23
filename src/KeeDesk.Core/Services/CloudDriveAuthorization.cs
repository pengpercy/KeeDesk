using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeeDesk.Core.Services;

public static class CloudDriveAuthorization
{
    internal static readonly HttpClient Network = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

    public static Task<CloudDriveClient?> ConnectAsync(CloudVaultProvider provider, string clientId, string? clientSecret,
        bool pickFile, Action<Uri> openBrowser, CancellationToken cancellationToken) =>
        ConnectAsync(provider, clientId, clientSecret, pickFile, openBrowser, Network, cancellationToken);

    internal static async Task<CloudDriveClient?> ConnectAsync(CloudVaultProvider provider, string clientId,
        string? clientSecret, bool pickFile, Action<Uri> openBrowser, HttpClient http, CancellationToken cancellationToken)
    {
        var (authorizationEndpoint, tokenEndpoint, scope, host) = provider switch
        {
            CloudVaultProvider.GoogleDrive => ("https://accounts.google.com/o/oauth2/v2/auth",
                "https://oauth2.googleapis.com/token", "https://www.googleapis.com/auth/drive.file", "127.0.0.1"),
            CloudVaultProvider.OneDrive => ("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize",
                "https://login.microsoftonline.com/consumers/oauth2/v2.0/token", "https://graph.microsoft.com/Files.ReadWrite.AppFolder", "localhost"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        if (string.IsNullOrWhiteSpace(clientId)) throw new ArgumentException("An OAuth client ID is required.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string callbackPath = provider == CloudVaultProvider.GoogleDrive ? "/keedesk-oauth/" : "/";
        string redirect = $"http://{host}:{port}" + (callbackPath == "/" ? "" : callbackPath);
        string state = RandomToken();
        string verifier = RandomToken();
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirect, ["response_type"] = "code",
            ["scope"] = scope, ["state"] = state, ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["prompt"] = "select_account"
        };

        if (provider == CloudVaultProvider.GoogleDrive && pickFile)
        {
            query["prompt"] = "consent";
            query["trigger_onepick"] = "true";
        }

        openBrowser(new Uri(authorizationEndpoint + "?" + string.Join("&", query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))));

        var callback = await ReceiveCallbackAsync(listener, callbackPath, state, token).ConfigureAwait(false);
        if (callback is null) return null;
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code",
            ["code"] = callback.Value.Code, ["code_verifier"] = verifier
        };
        if (provider == CloudVaultProvider.GoogleDrive && !string.IsNullOrWhiteSpace(clientSecret))
            fields["client_secret"] = clientSecret;

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var result = await CloudDriveClient.ReadJsonAsync(response, token).ConfigureAwait(false);
        if (!result.RootElement.TryGetProperty("access_token", out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } accessToken)
            throw new InvalidDataException("The OAuth response has no access token.");
        return new CloudDriveClient(provider, accessToken, http, callback.Value.FileId);
    }

    private static async Task<(string Code, string? FileId)?> ReceiveCallbackAsync(TcpListener listener, string path, string state,
        CancellationToken cancellation)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellation).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                using var stream = client.GetStream();
                string? target = await ReadRequestTargetAsync(stream, timeout.Token).ConfigureAwait(false);
                if (target is null || !target.StartsWith(path + "?", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, false, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                bool unique = true;
                foreach (var pair in target[(path.Length + 1)..].Split('&'))
                {
                    int separator = pair.IndexOf('=');
                    if (separator < 1) continue;
                    unique &= values.TryAdd(WebUtility.UrlDecode(pair[..separator]), WebUtility.UrlDecode(pair[(separator + 1)..]));
                }

                var responseState = values.GetValueOrDefault("state");
                bool matches = unique && responseState is not null &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(responseState), Encoding.UTF8.GetBytes(state));
                var code = values.GetValueOrDefault("code");
                bool denied = values.ContainsKey("error");
                if (!matches || (!denied && string.IsNullOrEmpty(code)))
                {
                    await RespondAsync(stream, false, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                await RespondAsync(stream, true, timeout.Token).ConfigureAwait(false);
                return denied ? null : (code!, values.GetValueOrDefault("picked_file_ids")?.Split(',')[0]);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            catch (IOException) when (!cancellation.IsCancellationRequested) { }
        }
    }

    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken cancellation)
    {
        byte[] buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancellation).ConfigureAwait(false);
            if (read == 0) return null;
            length += read;
            int end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            int firstLine = buffer.AsSpan(0, end + 2).IndexOf("\r\n"u8);
            var parts = Encoding.ASCII.GetString(buffer, 0, firstLine).Split(' ');
            return parts is ["GET", var target, "HTTP/1.1" or "HTTP/1.0"] ? target : null;
        }
        return null;
    }

    private static Task RespondAsync(NetworkStream stream, bool accepted, CancellationToken cancellation)
    {
        string body = accepted ? "Return to KeeDesk to continue. / 请返回 KeeDesk 继续。" : "Invalid callback.";
        var bytes = Encoding.UTF8.GetBytes(body);
        string status = accepted ? "200 OK" : "400 Bad Request";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\n\r\n");
        return stream.WriteAsync(header.Concat(bytes).ToArray(), cancellation).AsTask();
    }

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
