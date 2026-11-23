using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public static class DatabaseSynchronizationService
{
    public static KdbxDatabase? ReconcileFileChanges(KdbxDatabase current, CompositeKey key)
    {
        var originalPath = current.FilePath ?? throw new ArgumentException("Save the vault before reconciling changes.");
        var path = ResolvePath(originalPath);
        using var latest = KdbxFormat.Load(path, key);
        if (latest.RootGroup.Uuid != current.RootGroup.Uuid)
            throw new KdbxFileChangedException();
        if (!current.IsModified && current.SavedFileHash.AsSpan().SequenceEqual(latest.SavedFileHash))
            return null;

        var merged = current.CloneForSynchronization();
        try
        {
            DatabaseMergeService.Merge(merged, latest);
            merged.FilePath = path;
            merged.SavedFileHash = latest.SavedFileHash;
            KdbxFormat.Save(merged, path, key);
            merged.FilePath = originalPath;
            return merged;
        }
        catch
        {
            merged.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Synchronizes file-backed snapshots. The caller owns both returned databases.
    /// </summary>
    public static (KdbxDatabase Current, KdbxDatabase Other, bool Changed) Synchronize(
        KdbxDatabase current, CompositeKey currentKey, KdbxDatabase other, CompositeKey otherKey)
    {
        if (current.FilePath is null || other.FilePath is null ||
            current.SavedFileHash is null || other.SavedFileHash is null)
            throw new ArgumentException("Open both vault files before synchronizing.");

        string currentPath = ResolvePath(current.FilePath);
        string otherPath = ResolvePath(other.FilePath);
        if (KdbxFormat.FilePathComparer.Equals(currentPath, otherPath))
            throw new ArgumentException("Choose two different vault files.");

        var mergedCurrent = current.CloneForSynchronization();
        KdbxDatabase? mergedOther = null;
        try
        {
            mergedOther = other.CloneForSynchronization();
            mergedCurrent.FilePath = currentPath;
            mergedOther.FilePath = otherPath;
            DatabaseMergeService.Merge(mergedCurrent, mergedOther);
            // The first merge resolves equal timestamps and conflicting group moves. Apply that result to the replica.
            DatabaseMergeService.Merge(mergedOther, mergedCurrent, preferSourceOnTie: true);

            using var currentWrite = mergedCurrent.IsModified ? KdbxFormat.PrepareWrite(mergedCurrent, currentPath, currentKey) : null;
            using var otherWrite = mergedOther.IsModified ? KdbxFormat.PrepareWrite(mergedOther, otherPath, otherKey) : null;
            KdbxFormat.CommitSynchronizedPair(mergedCurrent, currentWrite, mergedOther, otherWrite);

            mergedCurrent.FilePath = current.FilePath;
            mergedOther.FilePath = other.FilePath;
            return (mergedCurrent, mergedOther, currentWrite is not null || otherWrite is not null);
        }
        catch
        {
            mergedCurrent.Dispose();
            mergedOther?.Dispose();
            throw;
        }
    }

    public static bool AreSameVaultFile(string first, string second) =>
        KdbxFormat.FilePathComparer.Equals(ResolvePath(first), ResolvePath(second));

    private static string ResolvePath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string current = Path.GetPathRoot(fullPath)!;
        var parts = fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            current = Path.Combine(current, parts[i]);
            FileSystemInfo info = i == parts.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
            current = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return current;
    }
}
