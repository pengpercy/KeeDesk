using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public sealed class VaultSynchronizationTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("keedesk-sync-");
    private readonly CompositeKey _localKey = CompositeKey.FromPassword("test local key");
    private readonly CompositeKey _otherKey = CompositeKey.FromPassword("test other key");
    private static readonly DateTimeOffset Epoch = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private string LocalPath => Path.Combine(_directory.FullName, "local.kdbx");
    private string OtherPath => Path.Combine(_directory.FullName, "other.kdbx");

    [Fact]
    public void ReconcileKeepsUnsavedChangesAndTheVersionDownloadedByTheCloudClient()
    {
        using var local = CreateVault("Shared vault");
        var edited = CreateEntry("Initial", 0);
        local.RootGroup.AddEntry(edited);
        KdbxFormat.Save(local, LocalPath, _localKey);
        using var downloaded = KdbxFormat.Load(LocalPath, _localKey);
        downloaded.RootGroup.AddEntry(CreateEntry("Downloaded", 1));
        KdbxFormat.Save(downloaded, LocalPath, _localKey);
        edited.Title = "Unsaved";
        edited.LastModificationTime = Epoch.AddSeconds(2);
        local.IsModified = true;

        using var merged = DatabaseSynchronizationService.ReconcileFileChanges(local, _localKey);
        Assert.NotNull(merged);
        using var disk = KdbxFormat.Load(LocalPath, _localKey);
        Assert.Equal("Unsaved", disk.FindEntryByUuid(edited.Uuid)?.Title);
        Assert.Contains(disk.RootGroup.Entries, entry => entry.Title == "Downloaded");
        Assert.Null(DatabaseSynchronizationService.ReconcileFileChanges(disk, _localKey));
        AssertNoScratchFiles();
    }

    [Fact]
    public void ReconcileRejectsAReplacementVaultWithTheSameKey()
    {
        using var local = CreateVault("Original");
        KdbxFormat.Save(local, LocalPath, _localKey);
        using var replacement = CreateVault("Different vault");
        KdbxFormat.Save(replacement, LocalPath, _localKey);
        Assert.Throws<KdbxFileChangedException>(() => DatabaseSynchronizationService.ReconcileFileChanges(local, _localKey));
        using var disk = KdbxFormat.Load(LocalPath, _localKey);
        Assert.Equal(replacement.RootGroup.Uuid, disk.RootGroup.Uuid);
        AssertNoScratchFiles();
    }

    [Fact]
    public void Synchronize_MergesIndependentEditsAndKeepsBothKeys_ThenBecomesNoOp()
    {
        using var local = CreateVault("Local vault");
        var from = new KdbxGroup { Name = "From", LocationChanged = Epoch, LastModificationTime = Epoch };
        var to = new KdbxGroup { Name = "To", LocationChanged = Epoch, LastModificationTime = Epoch };
        var deletedGroup = new KdbxGroup { Name = "Removed", LastModificationTime = Epoch };
        var entry = CreateEntry("Before both edits", 0);
        var deletedEntry = CreateEntry("Removed entry", 0);
        from.AddEntry(entry);
        deletedGroup.AddEntry(deletedEntry);
        local.RootGroup.AddGroup(from);
        local.RootGroup.AddGroup(to);
        local.RootGroup.AddGroup(deletedGroup);
        KdbxFormat.Save(local, LocalPath, _localKey);
        using var other = KdbxFormat.Load(LocalPath, _localKey);
        other.Meta.DatabaseName = "Other vault";
        other.Cipher = KdbxCipherAlgorithm.ChaCha20;
        KdbxFormat.Save(other, OtherPath, _otherKey);

        entry.Title = "Local edit";
        entry.UserName = "local";
        entry.LastModificationTime = Epoch.AddSeconds(3);
        entry.History.Add(CreateEntry("Local history", 1));
        local.RootGroup.AddEntry(CreateEntry("Local only", 1));
        local.IsModified = true;

        var otherEntry = other.FindEntryByUuid(entry.Uuid)!;
        var otherFrom = other.FindGroupByUuid(from.Uuid)!;
        var otherTo = other.FindGroupByUuid(to.Uuid)!;
        otherFrom.Entries.Remove(otherEntry);
        otherTo.AddEntry(otherEntry);
        otherEntry.PreviousParentGroup = otherFrom.Uuid;
        otherEntry.LocationChanged = Epoch.AddSeconds(2);
        otherEntry.Title = "Remote edit";
        otherEntry.UserName = "other";
        otherEntry.LastModificationTime = Epoch.AddSeconds(4);
        otherEntry.History.Add(CreateEntry("Remote history", 2));
        otherEntry.CustomData["integration"] = new KdbxCustomDataItem { Value = "Remote value" };
        var icon = new KdbxCustomIcon { Name = "Remote icon", Data = [10, 20, 30], LastModificationTime = Epoch.AddSeconds(4) };
        otherEntry.CustomIconUuid = icon.Uuid;
        other.Meta.CustomIcons.Add(icon);
        otherTo.CustomIconUuid = icon.Uuid;
        otherTo.LastModificationTime = Epoch.AddSeconds(4);
        other.RootGroup.AddEntry(CreateEntry("Other only", 2));
        other.DeleteGroupPermanently(other.FindGroupByUuid(deletedGroup.Uuid)!);

        var result = DatabaseSynchronizationService.Synchronize(local, _localKey, other, _otherKey);
        using var mergedLocal = result.Current;
        using var mergedOther = result.Other;
        Assert.True(result.Changed);
        Assert.Equal("Local edit", entry.Title);
        Assert.True(local.IsModified);
        Assert.Equal("Local vault", mergedLocal.Meta.DatabaseName);
        Assert.Equal("Other vault", mergedOther.Meta.DatabaseName);
        Assert.Equal(KdbxCipherAlgorithm.ChaCha20, mergedOther.Cipher);

        using var diskLocal = KdbxFormat.Load(LocalPath, _localKey);
        using var diskOther = KdbxFormat.Load(OtherPath, _otherKey);
        foreach (var vault in new[] { diskLocal, diskOther })
        {
            var merged = vault.FindEntryByUuid(entry.Uuid)!;
            Assert.Equal("Remote edit", merged.Title);
            Assert.Equal("other", merged.UserName);
            Assert.Equal(to.Uuid, merged.Parent!.Uuid);
            Assert.Equal(from.Uuid, merged.PreviousParentGroup);
            Assert.Equal("Remote value", merged.CustomData["integration"].Value);
            Assert.Equal(new[] { "Local history", "Remote history", "Local edit" }, merged.History.Select(item => item.Title));
            Assert.Equal(icon.Uuid, merged.CustomIconUuid);
            Assert.Equal(icon.Uuid, vault.FindGroupByUuid(to.Uuid)!.CustomIconUuid);
            Assert.Equal(new byte[] { 10, 20, 30 }, Assert.Single(vault.Meta.CustomIcons).Data);
            Assert.Null(vault.FindGroupByUuid(deletedGroup.Uuid));
            Assert.Null(vault.FindEntryByUuid(deletedEntry.Uuid));
            Assert.Contains(vault.RootGroup.Entries, item => item.Title == "Local only");
            Assert.Contains(vault.RootGroup.Entries, item => item.Title == "Other only");
        }

        byte[] initialLocal = File.ReadAllBytes(LocalPath);
        byte[] initialOther = File.ReadAllBytes(OtherPath);
        var repeated = DatabaseSynchronizationService.Synchronize(diskLocal, _localKey, diskOther, _otherKey);
        using var repeatLocal = repeated.Current;
        using var repeatOther = repeated.Other;
        Assert.False(repeated.Changed);
        Assert.Equal(initialLocal, File.ReadAllBytes(LocalPath));
        Assert.Equal(initialOther, File.ReadAllBytes(OtherPath));
        AssertNoScratchFiles();
    }

    [Fact]
    public void Synchronize_SameSecondEditsAndOpposingMoves_ConvergesWithoutLosingHistory()
    {
        using var local = CreateVault("Local vault");
        var left = new KdbxGroup { Name = "Left", LocationChanged = Epoch };
        var right = new KdbxGroup { Name = "Right", LocationChanged = Epoch };
        var entry = CreateEntry("Original", 0);
        local.RootGroup.AddGroup(left);
        local.RootGroup.AddGroup(right);
        left.AddEntry(entry);
        KdbxFormat.Save(local, LocalPath, _localKey);
        using var other = KdbxFormat.Load(LocalPath, _localKey);
        KdbxFormat.Save(other, OtherPath, _otherKey);

        local.RootGroup.Groups.Remove(left);
        right.AddGroup(left);
        left.LocationChanged = Epoch.AddSeconds(5);
        entry.Title = "Local wins the tie";
        entry.LastModificationTime = Epoch.AddSeconds(6);
        local.IsModified = true;

        var otherLeft = other.FindGroupByUuid(left.Uuid)!;
        var otherRight = other.FindGroupByUuid(right.Uuid)!;
        other.RootGroup.Groups.Remove(otherRight);
        otherLeft.AddGroup(otherRight);
        otherRight.LocationChanged = Epoch.AddSeconds(6);
        var otherEntry = other.FindEntryByUuid(entry.Uuid)!;
        otherEntry.Title = "Other version of entry";
        otherEntry.LastModificationTime = Epoch.AddSeconds(6);
        other.IsModified = true;

        var result = DatabaseSynchronizationService.Synchronize(local, _localKey, other, _otherKey);
        using var mergedLocal = result.Current;
        using var mergedOther = result.Other;
        foreach (var vault in new[] { mergedLocal, mergedOther })
        {
            Assert.Equal(vault.RootGroup.Uuid, vault.FindGroupByUuid(right.Uuid)!.Parent!.Uuid);
            Assert.Equal(right.Uuid, vault.FindGroupByUuid(left.Uuid)!.Parent!.Uuid);
            var merged = vault.FindEntryByUuid(entry.Uuid)!;
            Assert.Equal("Local wins the tie", merged.Title);
            Assert.Equal("Other version of entry", Assert.Single(merged.History).Title);
        }
    }

    [Fact]
    public void Synchronize_ExternalChangeAfterUnlock_LeavesBothFilesAndLocalEditsUntouched()
    {
        using var local = CreateVault("Local vault");
        using var other = CreateVault("Other vault");
        KdbxFormat.Save(local, LocalPath, _localKey);
        KdbxFormat.Save(other, OtherPath, _otherKey);
        using var external = KdbxFormat.Load(OtherPath, _otherKey);
        external.Meta.DatabaseName = "External edit";
        KdbxFormat.Save(external, OtherPath, _otherKey);
        byte[] originalLocal = File.ReadAllBytes(LocalPath);
        byte[] externalBytes = File.ReadAllBytes(OtherPath);
        local.RootGroup.AddEntry(CreateEntry("Draft", 0));
        local.IsModified = true;

        Assert.Throws<KdbxFileChangedException>(() => DatabaseSynchronizationService.Synchronize(local, _localKey, other, _otherKey));
        Assert.Equal(originalLocal, File.ReadAllBytes(LocalPath));
        Assert.Equal(externalBytes, File.ReadAllBytes(OtherPath));
        Assert.True(local.IsModified);
        Assert.Equal("Draft", Assert.Single(local.RootGroup.Entries).Title);
        AssertNoScratchFiles();
    }

    [Fact]
    public void CommitPair_SecondWriteFails_RestoresFirstFileByteForByte()
    {
        using var local = CreateVault("Local vault");
        using var other = CreateVault("Other vault");
        KdbxFormat.Save(local, LocalPath, _localKey);
        KdbxFormat.Save(other, OtherPath, _otherKey);
        byte[] originalLocal = File.ReadAllBytes(LocalPath);
        byte[] originalOther = File.ReadAllBytes(OtherPath);
        local.Meta.DatabaseName = "First attempted commit";
        local.IsModified = true;
        other.Meta.DatabaseName = "Second attempted commit";
        other.IsModified = true;

        using (var firstWrite = KdbxFormat.PrepareWrite(local, LocalPath, _localKey))
        using (var secondWrite = KdbxFormat.PrepareWrite(other, OtherPath, _otherKey))
        {
            File.Delete(secondWrite.TemporaryPath);
            Assert.ThrowsAny<IOException>(() => KdbxFormat.CommitSynchronizedPair(local, firstWrite, other, secondWrite));
            Assert.Equal(originalLocal, File.ReadAllBytes(LocalPath));
            Assert.Equal(originalOther, File.ReadAllBytes(OtherPath));
            Assert.True(local.IsModified);
            Assert.True(other.IsModified);
        }

        // The snapshots are still valid for retrying the operation.
        var retry = DatabaseSynchronizationService.Synchronize(local, _localKey, other, _otherKey);
        using var updatedLocal = retry.Current;
        using var updatedOther = retry.Other;
        Assert.True(retry.Changed);
        AssertNoScratchFiles();
    }

    [Fact]
    public void Synchronize_SamePath_IsRejectedWithoutWriting()
    {
        using var local = CreateVault("Local vault");
        KdbxFormat.Save(local, LocalPath, _localKey);
        using var alias = KdbxFormat.Load(Path.Combine(_directory.FullName, ".", "local.kdbx"), _localKey);
        byte[] original = File.ReadAllBytes(LocalPath);

        Assert.Throws<ArgumentException>(() => DatabaseSynchronizationService.Synchronize(local, _localKey, alias, _localKey));
        Assert.Equal(original, File.ReadAllBytes(LocalPath));
        AssertNoScratchFiles();
    }

    private static KdbxDatabase CreateVault(string name) => new()
    {
        Meta = new KdbxMeta { DatabaseName = name, RecycleBinEnabled = false },
        Kdf = KdbxKdfAlgorithm.AesKdf,
        AesKdfRounds = 1000
    };

    private static KdbxEntry CreateEntry(string title, int second) => new()
    {
        Title = title,
        CreationTime = Epoch,
        LocationChanged = Epoch,
        LastModificationTime = Epoch.AddSeconds(second)
    };

    private void AssertNoScratchFiles()
    {
        Assert.Empty(_directory.GetFiles("*.tmp"));
        Assert.Empty(_directory.GetFiles("KeeDesk-Sync-Recovery-*.kdbx"));
    }

    public void Dispose()
    {
        _localKey.Dispose();
        _otherKey.Dispose();
        _directory.Delete(recursive: true);
    }
}
