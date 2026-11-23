using System.Net;
using System.Net.Http.Headers;
using System.Text;
using KeeDesk.Core.Crypto;

namespace KeeDesk.Core.Services;

public sealed class RemoteVaultClient : IDisposable
{
    private static readonly HttpClient Network = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    });

    private readonly HttpClient _http;
    private readonly string _username;
    private readonly ProtectedString _password;
    private readonly object _credentialLock = new();

    public Uri Endpoint { get; }

    public RemoteVaultClient(Uri endpoint, string? username = null, string? password = null)
        : this(endpoint, username, password, Network) { }

    internal RemoteVaultClient(Uri endpoint, string? username, string? password, HttpClient http)
    {
        if (!IsSupportedEndpoint(endpoint) || username?.Contains(':') == true)
            throw new ArgumentException("Enter a complete HTTPS vault URL and a valid username.");
        Endpoint = endpoint;
        _http = http;
        _username = username ?? string.Empty;
        _password = new ProtectedString(true, password);
    }

    public static bool IsSupportedEndpoint(Uri? uri) =>
        uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Length > 0 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    public async Task<RemoteVaultVersion> DownloadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = CreateRequest(HttpMethod.Get);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("The server did not return a complete vault.", null, response.StatusCode);
        var data = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
        var tag = response.Headers.ETag;
        return new RemoteVaultVersion(data, tag is { IsWeak: false } && tag != EntityTagHeaderValue.Any ? tag : null);
    }

    public async Task<bool> TryReplaceAsync(byte[] encrypted, EntityTagHeaderValue? expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (expectedVersion is null || expectedVersion.IsWeak || expectedVersion == EntityTagHeaderValue.Any)
            throw new RemoteVaultVersionUnavailableException();

        using var request = CreateRequest(HttpMethod.Put);
        request.Headers.IfMatch.Add(expectedVersion);
        request.Content = new ByteArrayContent(encrypted);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            return false;
        response.EnsureSuccessStatusCode();
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
            throw new HttpRequestException("The server did not confirm the vault replacement.", null, response.StatusCode);
        return true;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, Endpoint);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        lock (_credentialLock)
        {
            if (_username.Length > 0 || !_password.IsEmpty)
            {
                var credentials = Encoding.UTF8.GetBytes(_username + ":" + _password.ReadString());
                try
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credentials));
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(credentials);
                }
            }
        }
        return request;
    }

    public void Dispose()
    {
        lock (_credentialLock)
            _password.Dispose();
    }
}

public sealed record RemoteVaultVersion(byte[] Encrypted, EntityTagHeaderValue? StrongETag);

public sealed class RemoteVaultVersionUnavailableException()
    : IOException("The remote server did not provide a strong ETag for conditional writes.");

public sealed class RemoteVaultConcurrentWriteException()
    : IOException("The remote vault changed repeatedly during synchronization.");
