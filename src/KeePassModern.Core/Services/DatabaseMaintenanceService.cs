using KeePassModern.Core.Kdbx;

namespace KeePassModern.Core.Services;

public sealed record DatabaseMaintenanceStats(
    int TotalEntries,
    int TotalGroups,
    int TotalHistoryItems,
    long TotalAttachmentBytes,
    int DeletedObjectsCount,
    int UnusedCustomIconsCount);

/// <summary>
/// 对标原版 KeePass 2.x DatabaseOperationsForm 与数据库维护工具的纯托管维护服务。
/// </summary>
public static class DatabaseMaintenanceService
{
    public static DatabaseMaintenanceStats GetStats(KdbxDatabase db)
    {
        var entries = db.EnumerateAllEntries(includeRecycleBin: true).ToList();
        var groups = db.EnumerateAllGroups(includeRecycleBin: true).ToList();

        int historyCount = entries.Sum(e => e.History.Count);
        long attachmentBytes = entries.Sum(e => e.Binaries.Values.Sum(b => (long)b.Length));
        int unusedIcons = CountUnusedCustomIcons(db, entries, groups);

        return new DatabaseMaintenanceStats(
            entries.Count,
            Math.Max(0, groups.Count - 1),
            historyCount,
            attachmentBytes,
            db.DeletedObjects.Count,
            unusedIcons);
    }

    /// <summary>
    /// 清理超过指定天数的历史记录快照（days &lt;= 0 时清理全部历史记录）。
    /// </summary>
    public static int PurgeHistoryOlderThan(KdbxDatabase db, int days)
    {
        int removed = 0;
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(Math.Max(0, days));
        db.Meta.MaintenanceHistoryDays = Math.Max(0, days);

        foreach (var entry in db.EnumerateAllEntries(includeRecycleBin: true))
        {
            for (int i = entry.History.Count - 1; i >= 0; i--)
            {
                var snap = entry.History[i];
                if (days <= 0 || snap.LastModificationTime <= cutoff)
                {
                    snap.Dispose();
                    entry.History.RemoveAt(i);
                    removed++;
                }
            }
        }

        if (removed > 0) db.IsModified = true;
        return removed;
    }

    /// <summary>
    /// 清空已删除对象记录（DeletedObjects），减小数据库体积（注意：清空后旧副本合并可能会恢复曾删除的对象）。
    /// </summary>
    public static int ClearDeletedObjects(KdbxDatabase db)
    {
        int count = db.DeletedObjects.Count;
        if (count == 0) return 0;
        db.DeletedObjects.Clear();
        db.IsModified = true;
        return count;
    }

    /// <summary>
    /// 删除全库完全重复的条目（标题、用户名、密码、网址、备注完全一致，保留最近修改的一条）。
    /// </summary>
    public static int DeleteDuplicateEntries(KdbxDatabase db)
    {
        var all = db.EnumerateAllEntries(includeRecycleBin: false).ToList();
        var groups = all.GroupBy(e =>
            $"{e.Title}\u001f{e.UserName}\u001f{e.Password}\u001f{e.Url}\u001f{e.Notes}",
            StringComparer.Ordinal);

        int removed = 0;
        foreach (var grp in groups)
        {
            var list = grp
                .OrderByDescending(e => e.LastModificationTime)
                .ThenByDescending(e => e.History.Count)
                .ToList();
            for (int i = 1; i < list.Count; i++)
            {
                db.DeleteEntryPermanently(list[i]);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// 递归清理不含任何条目和子分组的空分组（排除根分组与回收站）。
    /// </summary>
    public static int DeleteEmptyGroups(KdbxDatabase db)
    {
        int removed = 0;
        bool changed;
        do
        {
            changed = false;
            var empty = db.EnumerateAllGroups(includeRecycleBin: false)
                .Where(g => g != db.RootGroup &&
                            g.Uuid != db.Meta.RecycleBinUuid &&
                            g.Entries.Count == 0 &&
                            g.Groups.Count == 0)
                .ToList();
            foreach (var g in empty)
            {
                db.DeleteGroupPermanently(g);
                removed++;
                changed = true;
            }
        } while (changed);

        return removed;
    }

    /// <summary>
    /// 清理未被任何条目或分组引用的自定义图标。
    /// </summary>
    public static int DeleteUnusedCustomIcons(KdbxDatabase db)
    {
        if (db.Meta.CustomIcons.Count == 0) return 0;
        var used = CollectUsedIconUuids(
            db.EnumerateAllEntries(true),
            db.EnumerateAllGroups(true));
        int removed = db.Meta.CustomIcons.RemoveAll(i => !used.Contains(i.Uuid));
        if (removed > 0) db.IsModified = true;
        return removed;
    }

    /// <summary>
    /// 将指定条目移动到目标分组，同步更新位置变更时间与父引用。
    /// </summary>
    public static bool MoveEntryToGroup(KdbxDatabase db, KdbxEntry entry, KdbxGroup targetGroup)
    {
        if (entry.Parent == targetGroup || db.IsInRecycleBin(targetGroup))
            return false;

        if (entry.Parent is not null)
        {
            entry.PreviousParentGroup = entry.Parent.Uuid;
            entry.Parent.Entries.Remove(entry);
        }

        targetGroup.AddEntry(entry);
        var now = DateTimeOffset.UtcNow;
        entry.LocationChanged = now;
        entry.LastModificationTime = now;
        db.IsModified = true;
        return true;
    }

    private static int CountUnusedCustomIcons(KdbxDatabase db, List<KdbxEntry> entries, List<KdbxGroup> groups)
    {
        if (db.Meta.CustomIcons.Count == 0) return 0;
        var used = CollectUsedIconUuids(entries, groups);
        return db.Meta.CustomIcons.Count(i => !used.Contains(i.Uuid));
    }

    private static HashSet<Guid> CollectUsedIconUuids(IEnumerable<KdbxEntry> entries, IEnumerable<KdbxGroup> groups)
    {
        var used = new HashSet<Guid>();
        foreach (var e in entries)
        {
            if (e.CustomIconUuid != Guid.Empty) used.Add(e.CustomIconUuid);
            foreach (var h in e.History)
                if (h.CustomIconUuid != Guid.Empty) used.Add(h.CustomIconUuid);
        }
        foreach (var g in groups)
        {
            if (g.CustomIconUuid != Guid.Empty) used.Add(g.CustomIconUuid);
        }
        return used;
    }
}
