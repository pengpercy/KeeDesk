using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public sealed class CloudDriveSynchronizationTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Directory.CreateTempSubdirectory("keedesk-cloud-sync-").FullName;
    private readonly CompositeKey _key = CompositeKey.FromPassword("cloud sync fixture");
    private static readonly CloudDriveVault Origin = new("base", "Original.kdbx");

    [Theory]
    [InlineData(CloudVaultProvider.GoogleDrive)]
    [InlineData(CloudVaultProvider.OneDrive)]
    public async Task ConcurrentDevicesPreserveBothEditsAndConvergeThroughACheckpoint(CloudVaultProvider provider)
    {
        var seed = CreateVault();
        using var server = new MockDrive(provider, seed.Bytes, concurrentScans: 2);
        using var http = new HttpClient(server);
        using var clientA = new CloudDriveClient(provider, "token-a", http, provider == CloudVaultProvider.GoogleDrive ? Origin.Id : null);
        using var clientB = new CloudDriveClient(provider, "token-b", http);
        var pathA = WriteLocal("a", seed.Bytes);
        var pathB = WriteLocal("b", seed.Bytes);
        Edit(pathA, seed.EntryId, "Edit A", "Only A");
        Edit(pathB, seed.EntryId, "Edit B", "Only B");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await Task.WhenAll(Sync(pathA, clientA, timeout.Token), Sync(pathB, clientB, timeout.Token));
        Assert.Equal(2, server.RevisionCount);
        Assert.Equal(2, server.RevisionNames.Count(name => CloudDriveRevision.GetGeneration(name) == 1));
        Assert.Equal(seed.Bytes, server.OriginBytes);

        await Sync(pathA, clientA, timeout.Token);
        Assert.Equal(3, server.RevisionCount);
        Assert.Single(server.RevisionNames, name => CloudDriveRevision.GetGeneration(name) == 2);
        await Sync(pathB, clientB, timeout.Token);

        using var a = KdbxFormat.Load(pathA, _key);
        using var b = KdbxFormat.Load(pathB, _key);
        var first = a.FindEntryByUuid(seed.EntryId)!;
        var second = b.FindEntryByUuid(seed.EntryId)!;
        Assert.Equal(first.Title, second.Title);
        Assert.Contains(first.Title, new[] { "Edit A", "Edit B" });
        var otherEdit = first.Title == "Edit A" ? "Edit B" : "Edit A";
        Assert.Contains(first.History, entry => entry.Title == otherEdit);
        Assert.Contains(second.History, entry => entry.Title == otherEdit);
        Assert.Equal(new[] { "Only A", "Only B" }, a.EnumerateAllEntries().Select(entry => entry.Title)
            .Where(name => name.StartsWith("Only ", StringComparison.Ordinal)).Order());
        Assert.Equal(a.EnumerateAllEntries().Select(entry => entry.Title).Order(),
            b.EnumerateAllEntries().Select(entry => entry.Title).Order());

        server.Downloads.Clear();
        await Sync(pathA, clientA, timeout.Token);
        Assert.Equal(3, server.RevisionCount);
        Assert.Equal(2, server.Downloads.Count);
        Assert.Contains(Origin.Name, server.Downloads);
        Assert.Single(server.Downloads, name => CloudDriveRevision.GetGeneration(name) == 2);
        Assert.Equal(seed.Bytes, server.OriginBytes);
        Assert.Equal(Origin, Assert.Single(await clientB.ListAsync(timeout.Token)));
    }

    [Fact]
    public async Task UnrelatedVaultAndDamagedRevisionStopWithoutPublishing()
    {
        var seed = CreateVault();
        using var server = new MockDrive(CloudVaultProvider.GoogleDrive, seed.Bytes);
        using var http = new HttpClient(server);
        using var client = new CloudDriveClient(CloudVaultProvider.GoogleDrive, "test-token", http);
        var other = WriteLocal("unrelated", CreateVault().Bytes);
        await Assert.ThrowsAsync<CloudDriveVaultMismatchException>(() => Sync(other, client, CancellationToken.None));
        Assert.Equal(0, server.RevisionCount);

        var path = WriteLocal("local", seed.Bytes);
        Edit(path, seed.EntryId, "Local title", "Local only");
        var expected = await File.ReadAllBytesAsync(path);
        var prefix = CloudDriveRevision.GetPrefix(CloudVaultProvider.GoogleDrive, Origin.Id);
        var damaged = seed.Bytes.ToArray();
        damaged[^1] ^= 1;
        server.AddRevision(CloudDriveRevision.GetName(prefix, 1, seed.Bytes), damaged);

        await Assert.ThrowsAsync<CloudDriveRevisionException>(() => Sync(path, client, CancellationToken.None));
        Assert.Equal(1, server.RevisionCount);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UploadFailureKeepsLocalChangesAndCanBeRetried()
    {
        var seed = CreateVault();
        using var server = new MockDrive(CloudVaultProvider.OneDrive, seed.Bytes) { FailUpload = true };
        using var http = new HttpClient(server);
        using var client = new CloudDriveClient(CloudVaultProvider.OneDrive, "test-token", http);
        var path = WriteLocal("local", seed.Bytes);
        Edit(path, seed.EntryId, "Pending", "Local new");
        var expected = await File.ReadAllBytesAsync(path);

        await Assert.ThrowsAsync<HttpRequestException>(() => Sync(path, client, CancellationToken.None));
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, server.RevisionCount);
        server.FailUpload = false;
        await Sync(path, client, CancellationToken.None);
        Assert.Equal(1, server.RevisionCount);
        Assert.Equal(seed.Bytes, server.OriginBytes);
    }

    [Fact]
    public async Task ExternalLocalEditDuringListingPreventsCloudPublish()
    {
        var seed = CreateVault();
        using var server = new MockDrive(CloudVaultProvider.GoogleDrive, seed.Bytes);
        using var http = new HttpClient(server);
        using var client = new CloudDriveClient(CloudVaultProvider.GoogleDrive, "test-token", http);
        var path = WriteLocal("local", seed.Bytes);
        Edit(path, seed.EntryId, "Pending", "Local new");
        server.BeforeListing = () => Edit(path, seed.EntryId, "External edit", "External new");

        await Assert.ThrowsAsync<KdbxFileChangedException>(() => Sync(path, client, CancellationToken.None));
        Assert.Equal(0, server.RevisionCount);
        using var reopened = KdbxFormat.Load(path, _key);
        Assert.Equal("External edit", reopened.FindEntryByUuid(seed.EntryId)!.Title);
    }

    [Fact]
    public async Task LocalEditDuringUploadIsPreservedAndThePublishedRevisionRemainsAvailable()
    {
        var seed = CreateVault();
        var remotePath = WriteLocal("remote", seed.Bytes);
        Edit(remotePath, seed.EntryId, "Remote edit", "Remote only");
        using var server = new MockDrive(CloudVaultProvider.OneDrive, await File.ReadAllBytesAsync(remotePath));
        using var http = new HttpClient(server);
        using var client = new CloudDriveClient(CloudVaultProvider.OneDrive, "test-token", http);
        var localPath = WriteLocal("local", seed.Bytes);
        Edit(localPath, seed.EntryId, "Local edit", "Local only");
        server.BeforeUpload = () => Edit(localPath, seed.EntryId, "External edit", "External only");

        await Assert.ThrowsAsync<KdbxFileChangedException>(() => Sync(localPath, client, CancellationToken.None));
        Assert.Equal(1, server.RevisionCount);
        using (var reopened = KdbxFormat.Load(localPath, _key))
        {
            Assert.Equal("External edit", reopened.FindEntryByUuid(seed.EntryId)!.Title);
            Assert.DoesNotContain(reopened.EnumerateAllEntries(), entry => entry.Title == "Remote only");
        }

        server.BeforeUpload = null;
        await Sync(localPath, client, CancellationToken.None);
        using var merged = KdbxFormat.Load(localPath, _key);
        Assert.Contains(merged.EnumerateAllEntries(), entry => entry.Title == "Remote only");
        Assert.Contains(merged.EnumerateAllEntries(), entry => entry.Title == "External only");
    }

    private async Task Sync(string path, CloudDriveClient client, CancellationToken cancellation)
    {
        using var current = KdbxFormat.Load(path, _key);
        using var merged = await CloudDriveSynchronizationService.SynchronizeAsync(current, _key, client, Origin, cancellation);
        if (merged is not null)
            KdbxFormat.Save(merged, path, _key);
    }

    private (Guid EntryId, byte[] Bytes) CreateVault()
    {
        using var db = KdbxDatabase.CreateDefault("Test vault");
        db.Kdf = KdbxKdfAlgorithm.AesKdf;
        db.AesKdfRounds = 10;
        var entry = new KdbxEntry { Title = "Starting point", LastModificationTime = Created };
        db.RootGroup.AddEntry(entry);
        using var memory = new MemoryStream();
        KdbxFormat.Save(db, memory, _key);
        return (entry.Uuid, memory.ToArray());
    }

    private string WriteLocal(string name, byte[] bytes)
    {
        var path = Path.Combine(_folder, name + ".kdbx");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private void Edit(string path, Guid id, string title, string newEntry)
    {
        using var db = KdbxFormat.Load(path, _key);
        var entry = db.FindEntryByUuid(id)!;
        entry.Title = title;
        entry.LastModificationTime = Created.AddMinutes(1);
        db.RootGroup.AddEntry(new KdbxEntry { Title = newEntry });
        KdbxFormat.Save(db, path, _key);
    }

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    private sealed class MockDrive(CloudVaultProvider provider, byte[] origin, int concurrentScans = 0) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, (string Name, byte[] Bytes)> _revisions = new();
        private readonly ConcurrentDictionary<string, string> _uploads = new();
        private readonly TaskCompletionSource _bothScanned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _listingCount;
        private int _nextRevision;

        public ConcurrentQueue<string> Downloads { get; } = new();
        public int RevisionCount => _revisions.Count;
        public IEnumerable<string> RevisionNames => _revisions.Values.Select(value => value.Name);
        public byte[] OriginBytes { get; } = origin;
        public bool FailUpload { get; set; }
        public Action? BeforeListing { get; set; }
        public Action? BeforeUpload { get; set; }

        public void AddRevision(string name, byte[] bytes) =>
            Assert.True(_revisions.TryAdd($"rev-{Interlocked.Increment(ref _nextRevision)}", (name, bytes)));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            bool google = provider == CloudVaultProvider.GoogleDrive;
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/v1.0/me/drive/special/approot") return Json("""{"id":"folder"}""");
                if (path is "/drive/v3/files" or "/v1.0/me/drive/items/folder/children")
                {
                    var query = Uri.UnescapeDataString(uri.Query);
                    if (google && query.Contains("name contains", StringComparison.Ordinal))
                        Assert.Contains(CloudDriveRevision.GetPrefix(provider, Origin.Id), query);
                    BeforeListing?.Invoke();
                    var snapshot = _revisions.Select(item => (item.Key, item.Value.Name)).ToArray();
                    int scan = Interlocked.Increment(ref _listingCount);
                    if (scan <= concurrentScans)
                    {
                        if (scan == concurrentScans) _bothScanned.SetResult();
                        await _bothScanned.Task.WaitAsync(token);
                    }
                    var files = snapshot.Prepend((Origin.Id, Origin.Name))
                        .Select(file => $$"""{"id":"{{file.Item1}}","name":"{{file.Item2}}","file":{} }""");
                    var key = google ? "files" : "value";
                    return Json($"{{\"{key}\":[{string.Join(',', files)}]}}");
                }

                var segments = path.Split('/');
                var id = google ? segments[^1] : segments[^2];
                if (id == Origin.Id)
                {
                    Downloads.Enqueue(Origin.Name);
                    return Bytes(OriginBytes);
                }
                var revision = _revisions[id];
                Downloads.Enqueue(revision.Name);
                return Bytes(revision.Bytes);
            }

            Assert.DoesNotContain("/base", path);
            if (google && request.Method == HttpMethod.Post)
            {
                using var metadata = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                string uploadId = Guid.NewGuid().ToString("N");
                _uploads[uploadId] = metadata.RootElement.GetProperty("name").GetString()!;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Headers = { Location = new Uri($"https://www.googleapis.com/upload/drive/v3/files?upload_id={uploadId}") }
                };
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            if (FailUpload) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            string name = google ? _uploads[uri.Query["?upload_id=".Length..]]
                : Uri.UnescapeDataString(path.Split('/')[^2].TrimEnd(':'));
            Assert.True(CloudDriveRevision.IsRevisionName(name));
            BeforeUpload?.Invoke();
            AddRevision(name, await request.Content!.ReadAsByteArrayAsync(token));
            return Json("{}", HttpStatusCode.Created);
        }

        private static HttpResponseMessage Bytes(byte[] bytes) =>
            new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        private static HttpResponseMessage Json(string text, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
