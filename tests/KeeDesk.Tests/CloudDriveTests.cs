using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class CloudDriveTests
{
    [Theory]
    [InlineData(CloudVaultProvider.GoogleDrive)]
    [InlineData(CloudVaultProvider.OneDrive)]
    public async Task DesktopAuthorizationChecksStateAndExchangesPkceCode(CloudVaultProvider provider)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var browser = new HttpClient();
        Dictionary<string, string>? authorization = null;
        Task? callback = null;
        bool google = provider == CloudVaultProvider.GoogleDrive;
        using var network = new HttpClient(new MockCloud(async (request, cancellation) =>
        {
            Assert.Equal(google ? "oauth2.googleapis.com" : "login.microsoftonline.com", request.RequestUri!.Host);
            var form = ReadParameters(await request.Content!.ReadAsStringAsync(cancellation));
            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("synthetic-code", form["code"]);
            Assert.Equal(authorization!["redirect_uri"], form["redirect_uri"]);
            Assert.Equal(google, form.ContainsKey("client_secret"));
            string hash = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"])))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            Assert.Equal(authorization["code_challenge"], hash);
            return Json("""{"access_token":"test-token"}""");
        }));

        using var client = await CloudDriveAuthorization.ConnectAsync(provider, "test-client", "test-secret",
            pickFile: true, uri =>
            {
                authorization = ReadParameters(uri.Query);
                Assert.Equal("S256", authorization["code_challenge_method"]);
                Assert.Equal(google, authorization.ContainsKey("trigger_onepick"));
                Assert.Equal(google ? "https://www.googleapis.com/auth/drive.file" :
                    "https://graph.microsoft.com/Files.ReadWrite.AppFolder", authorization["scope"]);
                callback = SendCallbackAsync(authorization["redirect_uri"], authorization["state"]);
            }, network, timeout.Token);
        await callback!;
        Assert.NotNull(client);
        Assert.Equal(provider, client.Provider);

        async Task SendCallbackAsync(string redirect, string state)
        {
            using var invalid = await browser.GetAsync(redirect + "?code=wrong&state=wrong", timeout.Token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var accepted = await browser.GetAsync(
                redirect + "?code=synthetic-code&state=" + state + "&picked_file_ids=a_file", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }
    }

    [Fact]
    public async Task GooglePickerDownloadsOnlyTheAuthorizedKdbx()
    {
        var bytes = MakeEncryptedVault();
        var requests = new List<Uri>();
        using var http = new HttpClient(new MockCloud((request, _) =>
        {
            var uri = request.RequestUri!;
            requests.Add(uri);
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            Assert.Equal("/drive/v3/files/picked_id", uri.AbsolutePath);
            return Task.FromResult(uri.Query.Contains("alt=media")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : Json("""{"id":"picked_id","name":"Selected.kdbx","mimeType":"application/octet-stream"}"""));
        }));
        using var client = new CloudDriveClient(CloudVaultProvider.GoogleDrive, "test-token", http, "picked_id");
        var vault = Assert.Single(await client.ListAsync(CancellationToken.None));
        Assert.Equal("Selected.kdbx", vault.Name);
        Assert.Equal(bytes, await client.DownloadAsync(vault, CancellationToken.None));
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task GoogleCreatesAnEncryptedCopyWithoutSendingBearerToTransferUrl()
    {
        var calls = 0;
        var bytes = MakeEncryptedVault();
        using var http = new HttpClient(new MockCloud(async (request, token) =>
        {
            if (++calls == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Contains("uploadType=resumable", request.RequestUri!.Query);
                Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Assert.Equal("副本 \"Q\".kdbx", metadata.RootElement.GetProperty("name").GetString());
                return Redirect("https://www.googleapis.com/upload/drive/v3/files?upload_id=example", HttpStatusCode.OK);
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal(bytes, await request.Content!.ReadAsByteArrayAsync(token));
            return Json("""{"id":"copy"}""", HttpStatusCode.Created);
        }));
        using var client = new CloudDriveClient(CloudVaultProvider.GoogleDrive, "test-token", http);
        await client.UploadCopyAsync("副本 \"Q\".kdbx", bytes, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OneDriveListsOnlyKdbxFilesAndFollowsTheDownloadWithoutBearer()
    {
        var bytes = MakeEncryptedVault();
        var calls = new List<Uri>();
        using var http = new HttpClient(new MockCloud((request, _) =>
        {
            var uri = request.RequestUri!;
            calls.Add(uri);
            if (uri.Host != "graph.microsoft.com")
            {
                Assert.Equal("example.files.1drv.com", uri.Host);
                Assert.Null(request.Headers.Authorization);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }

            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            return Task.FromResult(uri.AbsolutePath switch
            {
                "/v1.0/me/drive/special/approot" => Json("""{"id":"F123!45"}"""),
                "/v1.0/me/drive/items/F123%2145/children" or "/v1.0/me/drive/items/F123!45/children" =>
                    uri.Query.Contains("next") ? Json("""{"value":[{"id":"next","name":"Other.kdbx","file":{}}]}""")
                    : Json("""
                        {"value":[{"id":"id1","name":"Vault.KDBX","file":{}},{"id":"dir","name":"Folder.kdbx","folder":{}}],
                        "@odata.nextLink":"https://graph.microsoft.com/v1.0/me/drive/items/F123!45/children?next"}
                        """),
                "/v1.0/me/drive/items/id1/content" => Redirect("https://example.files.1drv.com/content"),
                _ => throw new InvalidOperationException(uri.AbsolutePath)
            });
        }));
        using var client = new CloudDriveClient(CloudVaultProvider.OneDrive, "test-token", http);
        var vaults = await client.ListAsync(CancellationToken.None);
        Assert.Equal(["Other.kdbx", "Vault.KDBX"], vaults.Select(vault => vault.Name));
        Assert.Equal(bytes, await client.DownloadAsync(vaults[1], CancellationToken.None));
        Assert.Equal(5, calls.Count);
    }

    [Fact]
    public async Task OneDriveRequestsCreateOnlyAndSurfacesAConflict()
    {
        var calls = 0;
        using var http = new HttpClient(new MockCloud((request, _) =>
        {
            if (++calls == 1)
                return Task.FromResult(Json("""{"id":"folder"}"""));
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/v1.0/me/drive/items/folder:/Encrypted%20copy.kdbx:/content", request.RequestUri!.AbsolutePath);
            Assert.Equal("fail", ReadParameters(request.RequestUri.Query)["@microsoft.graph.conflictBehavior"]);
            Assert.Equal(EntityTagHeaderValue.Any, Assert.Single(request.Headers.IfNoneMatch));
            return Task.FromResult(Json("{}", HttpStatusCode.Conflict));
        }));
        using var client = new CloudDriveClient(CloudVaultProvider.OneDrive, "test-token", http);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.UploadCopyAsync("Encrypted copy.kdbx", MakeEncryptedVault(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OneDriveRejectsAnUnexpectedTransferHost()
    {
        int calls = 0;
        using var http = new HttpClient(new MockCloud((_, _) =>
        {
            calls++;
            return Task.FromResult(Redirect("https://files.1drv.com.example.test/file"));
        }));
        using var client = new CloudDriveClient(CloudVaultProvider.OneDrive, "test-token", http);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DownloadAsync(new CloudDriveVault("id1", "Vault.kdbx"), CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task UploadSnapshotUsesVerifiedSavedFileBytes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"keedesk-cloud-test-{Guid.NewGuid():N}.kdbx");
        try
        {
            await File.WriteAllBytesAsync(path, MakeEncryptedVault());
            using var key = CompositeKey.FromPassword("cloud fixture");
            using var vault = KdbxFormat.Load(path, key);
            Assert.Equal(await File.ReadAllBytesAsync(path), await CloudDriveClient.ReadSavedVaultAsync(vault, CancellationToken.None));
            await File.WriteAllBytesAsync(path, MakeEncryptedVault());
            await Assert.ThrowsAsync<KdbxFileChangedException>(() => CloudDriveClient.ReadSavedVaultAsync(vault, CancellationToken.None));
        }
        finally { File.Delete(path); }
    }

    private static Dictionary<string, string> ReadParameters(string input) => input.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).ToDictionary(pair => WebUtility.UrlDecode(pair[0]),
            pair => WebUtility.UrlDecode(pair[1]));

    private static byte[] MakeEncryptedVault()
    {
        using var vault = KdbxDatabase.CreateDefault("Cloud test");
        vault.Kdf = KdbxKdfAlgorithm.AesKdf;
        vault.AesKdfRounds = 10;
        using var key = CompositeKey.FromPassword("cloud fixture");
        using var stream = new MemoryStream();
        KdbxFormat.Save(vault, stream, key);
        return stream.ToArray();
    }

    private static HttpResponseMessage Json(string content, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string address, HttpStatusCode status = HttpStatusCode.Found) =>
        new(status) { Headers = { Location = new Uri(address) } };

    private sealed class MockCloud(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
