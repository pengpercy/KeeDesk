using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed class CloudDriveVaultMismatchException()
    : IOException("The selected cloud vault is not a copy of the local vault.");

public sealed class CloudDriveRevisionException()
    : IOException("A cloud vault revision is incomplete, damaged or has an unexpected name.");

internal static class CloudDriveRevision
{
    private const string Prefix = "KeeDesk-Sync-";
    private const string Extension = ".kdbx";
    private const int VaultIdLength = 32;
    private const int DigestLength = 64;
    private const int GenerationLength = 8;
    internal const int MaxGeneration = 99_999_999;

    internal static string GetPrefix(CloudVaultProvider provider, string fileId)
    {
        var id = SHA256.HashData(Encoding.UTF8.GetBytes($"{provider}:{fileId}"));
        return Prefix + Convert.ToHexStringLower(id.AsSpan(0, VaultIdLength / 2)) + "-";
    }

    internal static string GetName(string prefix, int generation, ReadOnlySpan<byte> encrypted) =>
        prefix + generation.ToString("D8", CultureInfo.InvariantCulture) + "-" +
        Convert.ToHexStringLower(SHA256.HashData(encrypted)) + Extension;

    internal static bool IsRevisionName(string name) => GetGeneration(name) > 0;

    internal static int GetGeneration(string name)
    {
        int vaultSeparator = Prefix.Length + VaultIdLength;
        int digestSeparator = vaultSeparator + 1 + GenerationLength;
        if (name.Length != digestSeparator + 1 + DigestLength + Extension.Length ||
            !name.StartsWith(Prefix, StringComparison.Ordinal) || name[vaultSeparator] != '-' ||
            name[digestSeparator] != '-' || !name.EndsWith(Extension, StringComparison.Ordinal) ||
            !IsLowerHex(name.AsSpan(Prefix.Length, VaultIdLength)) ||
            !IsLowerHex(name.AsSpan(digestSeparator + 1, DigestLength)))
            return 0;

        return int.TryParse(name.AsSpan(vaultSeparator + 1, GenerationLength), NumberStyles.None,
            CultureInfo.InvariantCulture, out int generation) ? generation : 0;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        return true;
    }
}

public static class CloudDriveSynchronizationService
{
    private const string IncludedRevisionsKey = "KeeDesk.CloudDrive.IncludedRevisions";

    public static Task<KdbxDatabase?> SynchronizeAsync(KdbxDatabase current, CompositeKey key, CloudDriveClient client,
        CloudDriveVault origin, CancellationToken cancellation = default)
    {
        if (current.FilePath is null || current.SavedFileHash is null || current.IsModified)
            throw new ArgumentException("Save the local vault before synchronizing with a cloud drive.");
        if (CloudDriveRevision.IsRevisionName(origin.Name))
            throw new ArgumentException("Select the original cloud vault.");
        cancellation.ThrowIfCancellationRequested();

        // Keep an independent snapshot while the UI can close or lock the current vault.
        var local = current.CloneForSynchronization();
        var retainedKey = key.Clone();
        return Task.Run(() => SynchronizeSnapshotAsync(local, retainedKey, client, origin, cancellation));
    }

    private static async Task<KdbxDatabase?> SynchronizeSnapshotAsync(KdbxDatabase local, CompositeKey key,
        CloudDriveClient client, CloudDriveVault origin, CancellationToken cancellation)
    {
        KdbxDatabase? cloud = null;
        bool keepLocal = false;
        try
        {
            KdbxFormat.VerifySavedSnapshot(local);
            var prefix = CloudDriveRevision.GetPrefix(client.Provider, origin.Id);
            using var baseline = await ReadVaultAsync(client, origin, key, local.RootGroup.Uuid, null, cancellation)
                .ConfigureAwait(false);
            var files = await client.ListRevisionsAsync(origin, cancellation).ConfigureAwait(false);
            var revisions = files.Select(file => (File: file, Generation: CloudDriveRevision.GetGeneration(file.Name)))
                .OrderByDescending(item => item.Generation).ThenBy(item => item.File.Name, StringComparer.Ordinal).ToArray();
            if (revisions.Any(item => item.Generation == 0))
                throw new CloudDriveRevisionException();

            var visible = revisions.Select(item => item.File.Name).ToHashSet(StringComparer.Ordinal);
            var included = new HashSet<string>(StringComparer.Ordinal);
            // A checkpoint already contains the revisions it names. Read the newest
            // checkpoint first and merge any concurrently published branches it missed.
            foreach (var (file, generation) in revisions)
            {
                if (!included.Add(file.Name)) continue;
                var incoming = await ReadVaultAsync(client, file, key, local.RootGroup.Uuid, prefix, cancellation, generation)
                    .ConfigureAwait(false);
                if (cloud is null)
                {
                    cloud = incoming;
                    AddIncludedRevisions(cloud, generation, visible, included);
                }
                else
                {
                    using (incoming)
                    {
                        AddIncludedRevisions(incoming, generation, visible, included);
                        DatabaseMergeService.Merge(cloud, incoming);
                    }
                }
            }

            if (cloud is null)
                cloud = baseline.CloneForSynchronization();
            else
                DatabaseMergeService.Merge(cloud, baseline);

            DatabaseMergeService.Merge(cloud, local);
            bool publish = cloud.IsModified;
            DatabaseMergeService.Merge(local, cloud, preferSourceOnTie: true);
            cancellation.ThrowIfCancellationRequested();
            KdbxFormat.VerifySavedSnapshot(local);

            if (publish)
            {
                int generation = revisions.FirstOrDefault().Generation + 1;
                if (generation > CloudDriveRevision.MaxGeneration) throw new CloudDriveRevisionException();
                cloud.Meta.CustomData[IncludedRevisionsKey] = new KdbxCustomDataItem
                {
                    Value = string.Join('\n', visible.Order(StringComparer.Ordinal))
                };
                using var output = new MemoryStream();
                KdbxFormat.Save(cloud, output, key);
                var bytes = output.ToArray();
                cancellation.ThrowIfCancellationRequested();
                KdbxFormat.VerifySavedSnapshot(local);
                await client.UploadCopyAsync(CloudDriveRevision.GetName(prefix, generation, bytes), bytes, cancellation).ConfigureAwait(false);
                KdbxFormat.VerifySavedSnapshot(local);
            }

            cancellation.ThrowIfCancellationRequested();
            keepLocal = local.IsModified;
            return keepLocal ? local : null;
        }
        finally
        {
            cloud?.Dispose();
            key.Dispose();
            if (!keepLocal) local.Dispose();
        }
    }

    private static void AddIncludedRevisions(KdbxDatabase vault, int generation, HashSet<string> visible, HashSet<string> included)
    {
        if (!vault.Meta.CustomData.TryGetValue(IncludedRevisionsKey, out var parents))
            return;
        foreach (var parent in parents.Value.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (visible.Contains(parent) && CloudDriveRevision.GetGeneration(parent) < generation)
                included.Add(parent);
        }
    }

    private static async Task<KdbxDatabase> ReadVaultAsync(CloudDriveClient client, CloudDriveVault file,
        CompositeKey key, Guid rootId, string? prefix, CancellationToken cancellation, int generation = 0)
    {
        var bytes = await client.DownloadAsync(file, cancellation).ConfigureAwait(false);
        if (prefix is not null && file.Name != CloudDriveRevision.GetName(prefix, generation, bytes))
            throw new CloudDriveRevisionException();
        cancellation.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(bytes, writable: false);
        var vault = KdbxFormat.Load(stream, key);
        if (vault.RootGroup.Uuid == rootId)
            return vault;

        vault.Dispose();
        if (prefix is not null)
            throw new CloudDriveRevisionException();
        throw new CloudDriveVaultMismatchException();
    }
}
