using System.Security.Cryptography;
using System.Text;

namespace KeeDesk.Core.Kdbx;

public sealed class KdbxSyncRollbackException(string recoveryPath, Exception writeError, Exception rollbackError)
    : IOException("Synchronization was interrupted. The original vault is available as an encrypted recovery file.",
        new AggregateException(writeError, rollbackError))
{
    public string RecoveryPath { get; } = recoveryPath;
}

public static partial class KdbxFormat
{
    internal static StringComparer FilePathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    public static void Save(KdbxDatabase db, string filePath, CompositeKey key)
    {
        filePath = Path.GetFullPath(filePath);
        byte[]? expectedHash = FilePathComparer.Equals(db.FilePath, filePath) && db.SavedFileHash is not null
            ? db.SavedFileHash
            : TryReadFileHash(filePath);

        using var write = PrepareWrite(db, filePath, key);
        using var commit = AcquireCommitLock(filePath);
        try
        {
            VerifyFileHash(filePath, expectedHash);
            File.Move(write.TemporaryPath, filePath, overwrite: expectedHash is not null);
            write.MarkSaved(db, filePath);
        }
        finally
        {
            commit.ReleaseMutex();
        }
    }

    internal static void VerifySavedSnapshot(KdbxDatabase database)
    {
        var path = database.FilePath ?? throw new ArgumentException("The vault has no local path.");
        using var commit = AcquireCommitLock(path);
        try { VerifyFileHash(path, database.SavedFileHash); }
        finally { commit.ReleaseMutex(); }
    }

    internal static PreparedWrite PrepareWrite(KdbxDatabase db, string filePath, CompositeKey key)
    {
        string temporaryPath = Path.Combine(Path.GetDirectoryName(filePath)!, $".keedesk-{Guid.NewGuid():N}.tmp");
        uint originalVersion = db.FormatVersion;
        bool wasModified = db.IsModified;
        bool ownsFile = false;
        try
        {
            byte[] savedHash;
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                ownsFile = true;
                Save(db, output, key);
                output.Flush(flushToDisk: true);
                output.Position = 0;
                savedHash = SHA256.HashData(output);
            }
            return new PreparedWrite(temporaryPath, savedHash, db.FormatVersion);
        }
        catch
        {
            if (ownsFile) DeleteTemporaryFile(temporaryPath);
            throw;
        }
        finally
        {
            db.FormatVersion = originalVersion;
            db.IsModified = wasModified;
        }
    }

    internal static void CommitSynchronizedPair(KdbxDatabase first, PreparedWrite? firstWrite,
        KdbxDatabase second, PreparedWrite? secondWrite)
    {
        string firstPath = first.FilePath!;
        string secondPath = second.FilePath!;
        if (FilePathComparer.Equals(firstPath, secondPath))
            throw new ArgumentException("Choose two different vault files.");

        var (lockPath1, lockPath2) = FilePathComparer.Compare(firstPath, secondPath) < 0
            ? (firstPath, secondPath)
            : (secondPath, firstPath);
        using var lock1 = AcquireCommitLock(lockPath1);
        try
        {
            using var lock2 = AcquireCommitLock(lockPath2);
            try
            {
                VerifyFileHash(firstPath, first.SavedFileHash);
                VerifyFileHash(secondPath, second.SavedFileHash);
                WriteSynchronizedPair(first, firstWrite, second, secondWrite);
            }
            finally
            {
                lock2.ReleaseMutex();
            }
        }
        finally
        {
            lock1.ReleaseMutex();
        }
    }

    private static void WriteSynchronizedPair(KdbxDatabase first, PreparedWrite? firstWrite,
        KdbxDatabase second, PreparedWrite? secondWrite)
    {
        if (firstWrite is null)
        {
            if (secondWrite is not null)
                InstallWrite(second, secondWrite);
            return;
        }
        if (secondWrite is null)
        {
            InstallWrite(first, firstWrite);
            return;
        }

        string firstPath = first.FilePath!;
        string backupPath = Path.Combine(Path.GetDirectoryName(firstPath)!, $"KeeDesk-Sync-Recovery-{Guid.NewGuid():N}.kdbx");
        bool ownsBackup = false;
        bool keepBackup = false;
        try
        {
            using (var original = File.OpenRead(firstPath))
            using (var backup = new FileStream(backupPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
                ownsBackup = true;
                original.CopyTo(backup);
                backup.Flush(flushToDisk: true);
                backup.Position = 0;
                if (!first.SavedFileHash.AsSpan().SequenceEqual(SHA256.HashData(backup)))
                    throw new KdbxFileChangedException();
            }

            VerifyFileHash(firstPath, first.SavedFileHash);
            VerifyFileHash(second.FilePath!, second.SavedFileHash);
            File.Move(firstWrite.TemporaryPath, firstPath, overwrite: true);
            try
            {
                InstallWrite(second, secondWrite);
            }
            catch (Exception writeError)
            {
                try
                {
                    // An independent writer may have modified the first file after its replacement.
                    VerifyFileHash(firstPath, firstWrite.Hash);
                    File.Move(backupPath, firstPath, overwrite: true);
                }
                catch (Exception rollbackError)
                {
                    keepBackup = true;
                    throw new KdbxSyncRollbackException(backupPath, writeError, rollbackError);
                }
                throw;
            }

            firstWrite.MarkSaved(first, firstPath);
        }
        finally
        {
            if (ownsBackup && !keepBackup) DeleteTemporaryFile(backupPath);
        }
    }

    private static void InstallWrite(KdbxDatabase db, PreparedWrite write)
    {
        string path = db.FilePath!;
        VerifyFileHash(path, db.SavedFileHash);
        File.Move(write.TemporaryPath, path, overwrite: true);
        write.MarkSaved(db, path);
    }

    private static Mutex AcquireCommitLock(string path)
    {
        if (FilePathComparer == StringComparer.OrdinalIgnoreCase)
            path = path.ToUpperInvariant();
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        var mutex = new Mutex(false, $"KeeDesk-Save-{pathHash}", new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false
        });
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(3)))
                throw new IOException("Another KeeDesk process is saving this vault. Try again.");
            return mutex;
        }
        catch (AbandonedMutexException)
        {
            // The mutex is now owned by this thread. Recheck the file before committing.
            return mutex;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    private static void VerifyFileHash(string filePath, byte[]? expectedHash)
    {
        byte[]? currentHash = TryReadFileHash(filePath);
        if ((expectedHash is null) != (currentHash is null) ||
            !expectedHash.AsSpan().SequenceEqual(currentHash))
            throw new KdbxFileChangedException();
    }

    private static byte[]? TryReadFileHash(string filePath)
    {
        try
        {
            using var input = File.OpenRead(filePath);
            return SHA256.HashData(input);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal sealed class PreparedWrite(string temporaryPath, byte[] hash, uint version) : IDisposable
    {
        public string TemporaryPath { get; } = temporaryPath;
        public byte[] Hash { get; } = hash;
        private bool _installed;

        public void MarkSaved(KdbxDatabase db, string path)
        {
            _installed = true;
            db.FilePath = path;
            db.SavedFileHash = Hash;
            db.FormatVersion = version;
            db.IsModified = false;
        }

        public void Dispose()
        {
            if (!_installed) DeleteTemporaryFile(TemporaryPath);
        }
    }
}
