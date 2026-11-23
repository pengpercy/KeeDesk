using KeeDesk.Core.Kdbx;

namespace KeeDesk.Tests;

public sealed class VaultFileSaveTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("keedesk-save-");
    private readonly CompositeKey _key = CompositeKey.FromPassword("test vault password");

    private string VaultPath => Path.Combine(_directory.FullName, "vault.kdbx");

    [Fact]
    public void Save_ExternallyChangedVaultWithSameTimestamp_RequiresSavingACopy()
    {
        using var original = CreateVault();
        DateTime originalTimestamp = DateTime.UtcNow.Date;
        File.SetLastWriteTimeUtc(VaultPath, originalTimestamp);
        using var external = KdbxFormat.Load(VaultPath, _key);
        external.Meta.DatabaseName = "External edits";
        KdbxFormat.Save(external, VaultPath, _key);
        File.SetLastWriteTimeUtc(VaultPath, originalTimestamp);
        Assert.Equal(originalTimestamp, File.GetLastWriteTimeUtc(VaultPath));
        byte[] externalBytes = File.ReadAllBytes(VaultPath);

        original.Meta.DatabaseName = "Local edits";
        original.IsModified = true;
        Assert.Throws<KdbxFileChangedException>(() => KdbxFormat.Save(original, VaultPath, _key));
        Assert.True(original.IsModified);
        Assert.Equal(externalBytes, File.ReadAllBytes(VaultPath));

        string copyPath = Path.Combine(_directory.FullName, "copy.kdbx");
        KdbxFormat.Save(original, copyPath, _key);
        original.Meta.DatabaseName = "More local edits";
        KdbxFormat.Save(original, copyPath, _key);

        using var recovered = KdbxFormat.Load(copyPath, _key);
        Assert.Equal("More local edits", recovered.Meta.DatabaseName);
        Assert.False(original.IsModified);
        Assert.Equal(externalBytes, File.ReadAllBytes(VaultPath));
        Assert.Empty(_directory.GetFiles("*.tmp"));
    }

    [Fact]
    public void Save_DeletedVault_DoesNotRecreateOriginal()
    {
        using var db = CreateVault();
        db.IsModified = true;
        File.Delete(VaultPath);

        Assert.Throws<KdbxFileChangedException>(() => KdbxFormat.Save(db, VaultPath, _key));
        Assert.True(db.IsModified);
        Assert.Empty(_directory.GetFiles());
    }

    [Fact]
    public async Task Save_CompetingSessions_OnlyOneCommits()
    {
        using var first = CreateVault();
        using var second = KdbxFormat.Load(VaultPath, _key);
        first.Meta.DatabaseName = "First edit";
        second.Meta.DatabaseName = "Second edit";
        first.IsModified = second.IsModified = true;

        var results = await Task.WhenAll(
            Task.Run(() => TrySave(first)),
            Task.Run(() => TrySave(second)));

        Assert.Single(results, saved => saved);
        Assert.True(first.IsModified ^ second.IsModified);
        using var loaded = KdbxFormat.Load(VaultPath, _key);
        Assert.Equal(results[0] ? "First edit" : "Second edit", loaded.Meta.DatabaseName);
        Assert.Empty(_directory.GetFiles("*.tmp"));
    }

    [Fact]
    public void Save_EncryptionFails_KeepsOriginalFileAndLocalEdits()
    {
        using var db = CreateVault();
        byte[] originalBytes = File.ReadAllBytes(VaultPath);
        db.Cipher = (KdbxCipherAlgorithm)int.MaxValue;
        db.IsModified = true;

        Assert.Throws<NotSupportedException>(() => KdbxFormat.Save(db, VaultPath, _key));
        Assert.True(db.IsModified);
        Assert.Equal(originalBytes, File.ReadAllBytes(VaultPath));
        Assert.Empty(_directory.GetFiles("*.tmp"));
    }

    private KdbxDatabase CreateVault()
    {
        var db = KdbxDatabase.CreateDefault("Test vault");
        db.Kdf = KdbxKdfAlgorithm.AesKdf;
        db.AesKdfRounds = 1000;
        KdbxFormat.Save(db, VaultPath, _key);
        return db;
    }

    private bool TrySave(KdbxDatabase database)
    {
        using var key = CompositeKey.FromPassword("test vault password");
        try
        {
            KdbxFormat.Save(database, VaultPath, key);
            return true;
        }
        catch (KdbxFileChangedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _key.Dispose();
        _directory.Delete(recursive: true);
    }
}
