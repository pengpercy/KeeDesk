using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public static class RemoteVaultSynchronizationService
{
    public static async Task<KdbxDatabase?> SynchronizeAsync(KdbxDatabase current, CompositeKey key,
        RemoteVaultClient client, CancellationToken cancellationToken = default)
    {
        if (current.FilePath is null || current.SavedFileHash is null)
            throw new ArgumentException("Save the local vault before synchronizing with a URL.");

        using var original = current.CloneForSynchronization();
        using var retainedKey = key.Clone();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var version = await client.DownloadAsync(cancellationToken).ConfigureAwait(false);
            var result = await Task.Run(() => Prepare(original, retainedKey, version), cancellationToken)
                .ConfigureAwait(false);
            var (merged, encrypted) = result;
            bool keepMerged = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                KdbxFormat.VerifySavedSnapshot(original);
                if (encrypted is not null &&
                    !await client.TryReplaceAsync(encrypted, version.StrongETag, cancellationToken).ConfigureAwait(false))
                    continue;

                cancellationToken.ThrowIfCancellationRequested();
                keepMerged = merged.IsModified;
                return keepMerged ? merged : null;
            }
            finally
            {
                if (!keepMerged) merged.Dispose();
            }
        }

        throw new RemoteVaultConcurrentWriteException();
    }

    private static (KdbxDatabase Local, byte[]? RemoteEncrypted) Prepare(KdbxDatabase original, CompositeKey key,
        RemoteVaultVersion version)
    {
        using var stream = new MemoryStream(version.Encrypted, writable: false);
        using var remote = KdbxFormat.Load(stream, key);
        var local = original.CloneForSynchronization();
        try
        {
            DatabaseMergeService.Merge(local, remote);
            DatabaseMergeService.Merge(remote, local, preferSourceOnTie: true);
            if (!remote.IsModified) return (local, null);
            if (version.StrongETag is null) throw new RemoteVaultVersionUnavailableException();

            using var replacement = new MemoryStream();
            KdbxFormat.Save(remote, replacement, key);
            return (local, replacement.ToArray());
        }
        catch
        {
            local.Dispose();
            throw;
        }
    }
}
