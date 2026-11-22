using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record MergeSummary(int EntriesAdded, int EntriesUpdated, int GroupsAdded, int ObjectsDeleted);

/// <summary>
/// 基于 UUID、最后修改时间与已删除对象记录（DeletedObjects）的 KeePass 无损数据库合并同步引擎。
/// </summary>
public static class DatabaseMergeService
{
    public static MergeSummary Merge(KdbxDatabase target, KdbxDatabase source)
    {
        int entriesAdded = 0, entriesUpdated = 0, groupsAdded = 0, deleted = 0;

        // 1. Merge DeletedObjects (keep newest deletion time per UUID)
        var targetDeleted = target.DeletedObjects.ToDictionary(d => d.Uuid, d => d);
        foreach (var srcDel in source.DeletedObjects)
        {
            if (!targetDeleted.TryGetValue(srcDel.Uuid, out var existing))
            {
                var copy = new KdbxDeletedObject { Uuid = srcDel.Uuid, DeletionTime = srcDel.DeletionTime };
                target.DeletedObjects.Add(copy);
                targetDeleted[copy.Uuid] = copy;
            }
            else if (srcDel.DeletionTime > existing.DeletionTime)
            {
                existing.DeletionTime = srcDel.DeletionTime;
            }
        }

        // 2. Apply deletions from source that are newer than target objects
        foreach (var del in source.DeletedObjects)
        {
            var entry = target.FindEntryByUuid(del.Uuid);
            if (entry is not null && del.DeletionTime > entry.LastModificationTime)
            {
                entry.Parent?.Entries.Remove(entry);
                entry.Dispose();
                deleted++;
            }
        }

        // 3. Merge groups & entries recursively
        MergeGroupRecursive(target, target.RootGroup, source.RootGroup, targetDeleted, ref groupsAdded, ref entriesAdded, ref entriesUpdated);

        if (entriesAdded > 0 || entriesUpdated > 0 || groupsAdded > 0 || deleted > 0)
            target.IsModified = true;

        return new MergeSummary(entriesAdded, entriesUpdated, groupsAdded, deleted);
    }

    private static void MergeGroupRecursive(
        KdbxDatabase targetDb,
        KdbxGroup targetParent,
        KdbxGroup sourceGroup,
        Dictionary<Guid, KdbxDeletedObject> deletedMap,
        ref int groupsAdded,
        ref int entriesAdded,
        ref int entriesUpdated)
    {
        foreach (var srcEntry in sourceGroup.Entries)
        {
            if (deletedMap.TryGetValue(srcEntry.Uuid, out var del) && del.DeletionTime > srcEntry.LastModificationTime)
                continue;

            var existing = targetDb.FindEntryByUuid(srcEntry.Uuid);
            if (existing is null)
            {
                var clone = srcEntry.CloneWithoutHistory();
                foreach (var h in srcEntry.History)
                    clone.History.Add(h.CloneWithoutHistory());
                targetParent.AddEntry(clone);
                entriesAdded++;
            }
            else if (srcEntry.LastModificationTime > existing.LastModificationTime)
            {
                existing.CreateHistorySnapshot(targetDb.Meta.HistoryMaxItems);
                existing.RestoreFromHistory(srcEntry, targetDb.Meta.HistoryMaxItems);
                existing.LastModificationTime = srcEntry.LastModificationTime;
                entriesUpdated++;
            }
        }

        foreach (var srcSub in sourceGroup.Groups)
        {
            if (deletedMap.TryGetValue(srcSub.Uuid, out var del) && del.DeletionTime > srcSub.LastModificationTime)
                continue;

            var existingGroup = targetDb.FindGroupByUuid(srcSub.Uuid);
            if (existingGroup is null)
            {
                existingGroup = new KdbxGroup
                {
                    Uuid = srcSub.Uuid,
                    Name = srcSub.Name,
                    Notes = srcSub.Notes,
                    IconId = srcSub.IconId,
                    CustomIconUuid = srcSub.CustomIconUuid,
                    Tags = new List<string>(srcSub.Tags),
                    CreationTime = srcSub.CreationTime,
                    LastModificationTime = srcSub.LastModificationTime
                };
                targetParent.AddGroup(existingGroup);
                groupsAdded++;
            }
            else if (srcSub.LastModificationTime > existingGroup.LastModificationTime)
            {
                existingGroup.Name = srcSub.Name;
                existingGroup.Notes = srcSub.Notes;
                existingGroup.IconId = srcSub.IconId;
                existingGroup.LastModificationTime = srcSub.LastModificationTime;
            }

            MergeGroupRecursive(targetDb, existingGroup, srcSub, deletedMap, ref groupsAdded, ref entriesAdded, ref entriesUpdated);
        }
    }
}
