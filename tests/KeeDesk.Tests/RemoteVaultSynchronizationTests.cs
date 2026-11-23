using System.Net;
using System.Net.Http.Headers;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class RemoteVaultSynchronizationTests
{
    [Fact]
    public async Task SynchronizationRetriesWithoutOverwritingAConcurrentWrite()
    {
        using var fixture = new VaultFixture();
        using var remote = KdbxFormat.Load(fixture.Path, fixture.Key);
        fixture.Local.RootGroup.AddEntry(new KdbxEntry { Title = "Local edit" });
        KdbxFormat.Save(fixture.Local, fixture.Path, fixture.Key);
        remote.RootGroup.AddEntry(new KdbxEntry { Title = "Remote edit" });
        var handler = new WebDavHandler(Encrypt(remote, fixture.Key))
        {
            BeforeFirstWrite = () =>
            {
                remote.RootGroup.AddEntry(new KdbxEntry { Title = "Another user" });
                return Encrypt(remote, fixture.Key);
            }
        };
        using var http = new HttpClient(handler);
        using var client = new RemoteVaultClient(new Uri("https://example.test/vault.kdbx"), "sample", "test", http);
        using var merged = await RemoteVaultSynchronizationService.SynchronizeAsync(fixture.Local, fixture.Key, client);
        Assert.NotNull(merged);
        KdbxFormat.Save(merged, fixture.Path, fixture.Key);
        Assert.Equal(2, handler.ConditionalWrites.Count);
        Assert.Equal(["\"1\"", "\"2\""], handler.ConditionalWrites);

        using var mergedRemote = KdbxFormat.Load(new MemoryStream(handler.Content), fixture.Key);
        using var mergedLocal = KdbxFormat.Load(fixture.Path, fixture.Key);
        var expected = new[] { "Another user", "Local edit", "Remote edit" };
        Assert.Equal(expected, Titles(mergedRemote));
        Assert.Equal(expected, Titles(mergedLocal));
        Assert.All(handler.Authorizations, header => Assert.Equal("Basic", header?.Scheme));

        int writes = handler.ConditionalWrites.Count;
        Assert.Null(await RemoteVaultSynchronizationService.SynchronizeAsync(mergedLocal, fixture.Key, client));
        Assert.Equal(writes, handler.ConditionalWrites.Count);
    }

    [Fact]
    public async Task WeakValidatorOrOfflineLeavesLocalVaultIntact()
    {
        using var fixture = new VaultFixture();
        var handler = new WebDavHandler(File.ReadAllBytes(fixture.Path)) { WeakValidator = true };
        fixture.Local.RootGroup.AddEntry(new KdbxEntry { Title = "Pending edit" });
        KdbxFormat.Save(fixture.Local, fixture.Path, fixture.Key);
        using var http = new HttpClient(handler);
        using var client = new RemoteVaultClient(new Uri("https://example.test/vault.kdbx"), null, null, http);

        await Assert.ThrowsAsync<RemoteVaultVersionUnavailableException>(
            () => RemoteVaultSynchronizationService.SynchronizeAsync(fixture.Local, fixture.Key, client));
        Assert.Empty(handler.ConditionalWrites);
        handler.Offline = true;
        await Assert.ThrowsAsync<HttpRequestException>(
            () => RemoteVaultSynchronizationService.SynchronizeAsync(fixture.Local, fixture.Key, client));
        using var saved = KdbxFormat.Load(fixture.Path, fixture.Key);
        Assert.Equal(["Pending edit"], Titles(saved));
        Assert.False(RemoteVaultClient.IsSupportedEndpoint(new Uri("http://example.test/file")));
        Assert.False(RemoteVaultClient.IsSupportedEndpoint(new Uri("https://user:secret@example.test/file")));
    }

    private static string[] Titles(KdbxDatabase vault) =>
        vault.EnumerateAllEntries().Select(entry => entry.Title).OrderBy(title => title).ToArray();

    private static byte[] Encrypt(KdbxDatabase vault, CompositeKey key)
    {
        using var output = new MemoryStream();
        KdbxFormat.Save(vault, output, key);
        return output.ToArray();
    }

    private sealed class WebDavHandler(byte[] content) : HttpMessageHandler
    {
        private int _version = 1;
        public byte[] Content { get; private set; } = content;
        public Func<byte[]>? BeforeFirstWrite { get; init; }
        public bool WeakValidator { get; init; }
        public bool Offline { get; set; }
        public List<string> ConditionalWrites { get; } = [];
        public List<AuthenticationHeaderValue?> Authorizations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Offline) throw new HttpRequestException("Offline");
            Authorizations.Add(request.Headers.Authorization);
            if (request.Method == HttpMethod.Get)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) };
                response.Headers.ETag = new EntityTagHeaderValue($"\"{_version}\"", WeakValidator);
                return response;
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            var expectedVersion = Assert.Single(request.Headers.IfMatch);
            ConditionalWrites.Add(expectedVersion.Tag);
            if (ConditionalWrites.Count == 1 && BeforeFirstWrite is not null)
            {
                Content = BeforeFirstWrite();
                _version++;
            }
            if (expectedVersion.Tag != $"\"{_version}\"")
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);

            Content = await request.Content!.ReadAsByteArrayAsync(token);
            _version++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class VaultFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"KeeDesk-Remote-{Guid.NewGuid():N}");
        public string Path { get; }
        public CompositeKey Key { get; } = CompositeKey.FromPassword("test");
        public KdbxDatabase Local { get; } = new() { Kdf = KdbxKdfAlgorithm.AesKdf, AesKdfRounds = 1000 };

        public VaultFixture()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "vault.kdbx");
            KdbxFormat.Save(Local, Path, Key);
        }

        public void Dispose()
        {
            Local.Dispose();
            Key.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
