using System.Security.Cryptography;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record MergeSummary(int EntriesAdded, int EntriesUpdated, int GroupsAdded, int ObjectsDeleted);

public static class DatabaseMergeService
{
    public static MergeSummary Merge(KdbxDatabase target, KdbxDatabase source) =>
        Merge(target, source, preferSourceOnTie: false);

    internal static MergeSummary Merge(KdbxDatabase target, KdbxDatabase source, bool preferSourceOnTie)
    {
        if (ReferenceEquals(target, source)) return new MergeSummary(0, 0, 0, 0);

        var deletions = target.DeletedObjects.ToDictionary(item => item.Uuid);
        bool changed = MergeDeletions(target, source, deletions);
        changed |= MergeIcons(target, source, preferSourceOnTie);

        var groups = target.EnumerateAllGroups().ToDictionary(group => group.Uuid);
        var sourceGroups = source.EnumerateAllGroups().Skip(1).ToList();
        groups[source.RootGroup.Uuid] = target.RootGroup;
        Guid mappedSourceBin = Guid.Empty;
        if (target.Meta.RecycleBinEnabled && source.Meta.RecycleBinEnabled &&
            source.Meta.RecycleBinUuid != Guid.Empty &&
            source.Meta.RecycleBinUuid != target.Meta.RecycleBinUuid &&
            groups.TryGetValue(target.Meta.RecycleBinUuid, out var targetBin))
        {
            mappedSourceBin = source.Meta.RecycleBinUuid;
            groups[mappedSourceBin] = targetBin;
        }

        int groupsAdded = 0;
        foreach (var incoming in sourceGroups)
        {
            if (groups.TryGetValue(incoming.Uuid, out var current))
            {
                if (current != target.RootGroup && incoming.Uuid != mappedSourceBin)
                    changed |= MergeGroupProperties(current, incoming, preferSourceOnTie);
                continue;
            }

            var copy = incoming.CloneWithoutChildren();
            copy.PreviousParentGroup = MapGroup(groups, copy.PreviousParentGroup);
            groups[incoming.Parent!.Uuid].AddGroup(copy);
            groups.Add(incoming.Uuid, copy);
            groupsAdded++;
        }

        foreach (var incoming in sourceGroups)
        {
            var current = groups[incoming.Uuid];
            var parent = groups[incoming.Parent!.Uuid];
            if (current == target.RootGroup || incoming.Uuid == mappedSourceBin ||
                (!preferSourceOnTie && !Wins(incoming.LocationChanged, current.LocationChanged, preferSourceOnTie: false)) ||
                IsWithin(parent, current))
                continue;

            Guid previousParent = MapGroup(groups, incoming.PreviousParentGroup);
            if (current.Parent != parent)
            {
                current.Parent!.Groups.Remove(current);
                parent.AddGroup(current);
                changed = true;
            }
            changed |= current.LocationChanged != incoming.LocationChanged || current.PreviousParentGroup != previousParent;
            current.LocationChanged = incoming.LocationChanged;
            current.PreviousParentGroup = previousParent;
        }

        var entries = target.EnumerateAllEntries(includeRecycleBin: true).ToDictionary(entry => entry.Uuid);
        int entriesAdded = 0;
        int entriesUpdated = 0;
        foreach (var incoming in source.EnumerateAllEntries(includeRecycleBin: true))
        {
            var parent = groups[incoming.Parent!.Uuid];
            Guid previousParent = MapGroup(groups, incoming.PreviousParentGroup);
            if (!entries.TryGetValue(incoming.Uuid, out var current))
            {
                if (deletions.TryGetValue(incoming.Uuid, out var deletion) && deletion.DeletionTime > incoming.LastModificationTime)
                    continue;
                var copy = incoming.CloneWithHistory();
                copy.PreviousParentGroup = previousParent;
                parent.AddEntry(copy);
                entriesAdded++;
                continue;
            }

            bool entryChanged = MergeEntryContent(current, incoming, preferSourceOnTie);
            if (preferSourceOnTie || Wins(incoming.LocationChanged, current.LocationChanged, preferSourceOnTie: false))
            {
                if (current.Parent != parent)
                {
                    current.Parent!.Entries.Remove(current);
                    parent.AddEntry(current);
                    entryChanged = true;
                }
                entryChanged |= current.LocationChanged != incoming.LocationChanged || current.PreviousParentGroup != previousParent;
                current.LocationChanged = incoming.LocationChanged;
                current.PreviousParentGroup = previousParent;
            }

            if (entryChanged) entriesUpdated++;
        }

        int deleted = ApplyDeletions(target, deletions);
        if (changed || entriesAdded > 0 || entriesUpdated > 0 || groupsAdded > 0 || deleted > 0)
            target.IsModified = true;
        return new MergeSummary(entriesAdded, entriesUpdated, groupsAdded, deleted);

    }

    private static bool MergeDeletions(KdbxDatabase target, KdbxDatabase source,
        Dictionary<Guid, KdbxDeletedObject> deletions)
    {
        bool changed = false;
        foreach (var incoming in source.DeletedObjects)
        {
            if (deletions.TryGetValue(incoming.Uuid, out var current))
            {
                if (incoming.DeletionTime <= current.DeletionTime) continue;
                current.DeletionTime = incoming.DeletionTime;
            }
            else
            {
                var copy = new KdbxDeletedObject { Uuid = incoming.Uuid, DeletionTime = incoming.DeletionTime };
                target.DeletedObjects.Add(copy);
                deletions.Add(copy.Uuid, copy);
            }
            changed = true;
        }
        return changed;
    }

    private static int ApplyDeletions(KdbxDatabase target, Dictionary<Guid, KdbxDeletedObject> deletions)
    {
        int deleted = 0;
        foreach (var entry in target.EnumerateAllEntries(includeRecycleBin: true).ToList())
        {
            if (!deletions.TryGetValue(entry.Uuid, out var item) || item.DeletionTime <= entry.LastModificationTime)
                continue;
            entry.Parent!.Entries.Remove(entry);
            entry.Dispose();
            deleted++;
        }

        foreach (var group in target.EnumerateAllGroups().Skip(1).Reverse().ToList())
        {
            // A concurrently added or edited descendant takes precedence over deleting its container.
            if (group.Entries.Count != 0 || group.Groups.Count != 0 ||
                !deletions.TryGetValue(group.Uuid, out var item) || item.DeletionTime <= group.LastModificationTime)
                continue;
            group.Parent!.Groups.Remove(group);
            group.Dispose();
            deleted++;
        }
        return deleted;
    }

    private static bool MergeIcons(KdbxDatabase target, KdbxDatabase source, bool preferSourceOnTie)
    {
        var icons = target.Meta.CustomIcons;
        var positions = icons.Select((icon, index) => (icon.Uuid, index)).ToDictionary(pair => pair.Uuid, pair => pair.index);
        bool changed = false;
        foreach (var incoming in source.Meta.CustomIcons)
        {
            if (!positions.TryGetValue(incoming.Uuid, out int index))
            {
                positions.Add(incoming.Uuid, icons.Count);
                icons.Add(incoming.Clone());
                changed = true;
            }
            else
            {
                var current = icons[index];
                if (!Wins(incoming.LastModificationTime ?? DateTimeOffset.MinValue,
                        current.LastModificationTime ?? DateTimeOffset.MinValue, preferSourceOnTie) ||
                    (incoming.LastModificationTime == current.LastModificationTime && incoming.Name == current.Name &&
                     incoming.Data.AsSpan().SequenceEqual(current.Data)))
                    continue;
                icons[index] = incoming.Clone();
                changed = true;
            }
        }
        return changed;
    }

    private static bool MergeGroupProperties(KdbxGroup current, KdbxGroup incoming, bool preferSourceOnTie)
    {
        if (!Wins(incoming.LastModificationTime, current.LastModificationTime, preferSourceOnTie))
            return false;
        if (current.LastModificationTime == incoming.LastModificationTime &&
            current.Name == incoming.Name && current.Notes == incoming.Notes &&
            current.IconId == incoming.IconId && current.CustomIconUuid == incoming.CustomIconUuid &&
            current.DefaultAutoTypeSequence == incoming.DefaultAutoTypeSequence &&
            current.EnableAutoType == incoming.EnableAutoType && current.EnableSearching == incoming.EnableSearching &&
            current.CreationTime == incoming.CreationTime && current.ExpiryTime == incoming.ExpiryTime &&
            current.Expires == incoming.Expires && current.Tags.SequenceEqual(incoming.Tags) &&
            SameCustomData(current.CustomData, incoming.CustomData))
            return false;

        current.Name = incoming.Name;
        current.Notes = incoming.Notes;
        current.IconId = incoming.IconId;
        current.CustomIconUuid = incoming.CustomIconUuid;
        current.DefaultAutoTypeSequence = incoming.DefaultAutoTypeSequence;
        current.EnableAutoType = incoming.EnableAutoType;
        current.EnableSearching = incoming.EnableSearching;
        current.CreationTime = incoming.CreationTime;
        current.ExpiryTime = incoming.ExpiryTime;
        current.Expires = incoming.Expires;
        current.LastModificationTime = incoming.LastModificationTime;
        current.Tags = incoming.Tags.ToList();
        CopyCustomData(current.CustomData, incoming.CustomData);
        return true;
    }

    private static bool MergeEntryContent(KdbxEntry current, KdbxEntry incoming, bool preferSourceOnTie)
    {
        bool changed = false;
        foreach (var snapshot in incoming.History)
            changed |= AddHistory(current, snapshot);

        if (Wins(incoming.LastModificationTime, current.LastModificationTime, preferSourceOnTie))
        {
            if (!SameContent(current, incoming))
            {
                AddHistory(current, current);
                current.CopyContentFrom(incoming);
                current.CreationTime = incoming.CreationTime;
                CopyCustomData(current.CustomData, incoming.CustomData);
                changed = true;
            }
            changed |= current.LastModificationTime != incoming.LastModificationTime;
            current.LastModificationTime = incoming.LastModificationTime;
        }
        else if (!SameContent(current, incoming))
        {
            changed |= AddHistory(current, incoming);
        }

        if (changed)
        {
            var sorted = current.History.OrderBy(snapshot => snapshot.LastModificationTime).ToList();
            current.History.Clear();
            current.History.AddRange(sorted);
        }
        return changed;
    }

    private static bool AddHistory(KdbxEntry entry, KdbxEntry snapshot)
    {
        long time = snapshot.LastModificationTime.ToUnixTimeSeconds();
        if (entry.History.Any(old => old.LastModificationTime.ToUnixTimeSeconds() == time && SameContent(old, snapshot)))
            return false;
        entry.History.Add(snapshot.CloneWithoutHistory());
        return true;
    }

    private static bool SameContent(KdbxEntry left, KdbxEntry right) =>
        left.IconId == right.IconId && left.CustomIconUuid == right.CustomIconUuid &&
        left.ForegroundColor == right.ForegroundColor && left.BackgroundColor == right.BackgroundColor &&
        left.OverrideUrl == right.OverrideUrl && left.QualityCheck == right.QualityCheck &&
        left.CreationTime.ToUnixTimeSeconds() == right.CreationTime.ToUnixTimeSeconds() &&
        left.Expires == right.Expires && (!left.Expires || left.ExpiryTime.ToUnixTimeSeconds() == right.ExpiryTime.ToUnixTimeSeconds()) &&
        left.Tags.SequenceEqual(right.Tags) && left.AutoType.Enabled == right.AutoType.Enabled &&
        left.AutoType.DataTransferObfuscation == right.AutoType.DataTransferObfuscation &&
        left.AutoType.DefaultSequence == right.AutoType.DefaultSequence &&
        left.AutoType.Associations.Select(item => (item.Window, item.KeystrokeSequence))
            .SequenceEqual(right.AutoType.Associations.Select(item => (item.Window, item.KeystrokeSequence))) &&
        SameCustomData(left.CustomData, right.CustomData) &&
        left.Strings.Count == right.Strings.Count &&
        left.Strings.All(pair => right.Strings.TryGetValue(pair.Key, out var value) &&
            pair.Value.IsProtected == value.IsProtected && pair.Value.Length == value.Length &&
            SameBytes(pair.Value.ReadUtf8(), value.ReadUtf8())) &&
        left.Binaries.Count == right.Binaries.Count &&
        left.Binaries.All(pair => right.Binaries.TryGetValue(pair.Key, out var value) &&
            pair.Value.IsProtected == value.IsProtected && pair.Value.Length == value.Length &&
            SameBytes(pair.Value.ReadData(), value.ReadData()));

    private static bool SameBytes(byte[] left, byte[] right)
    {
        try { return left.AsSpan().SequenceEqual(right); }
        finally
        {
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }

    private static bool SameCustomData(Dictionary<string, KdbxCustomDataItem> left,
        Dictionary<string, KdbxCustomDataItem> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) &&
            pair.Value.Value == value.Value &&
            pair.Value.LastModificationTime?.ToUnixTimeSeconds() == value.LastModificationTime?.ToUnixTimeSeconds());

    private static void CopyCustomData(Dictionary<string, KdbxCustomDataItem> target,
        Dictionary<string, KdbxCustomDataItem> source)
    {
        target.Clear();
        foreach (var (key, value) in source)
            target.Add(key, value.Clone());
    }

    private static bool Wins(DateTimeOffset incoming, DateTimeOffset current, bool preferSourceOnTie)
    {
        int order = incoming.ToUnixTimeSeconds().CompareTo(current.ToUnixTimeSeconds());
        return order > 0 || (order == 0 && preferSourceOnTie);
    }

    private static Guid MapGroup(Dictionary<Guid, KdbxGroup> groups, Guid uuid) =>
        groups.TryGetValue(uuid, out var group) ? group.Uuid : uuid;

    private static bool IsWithin(KdbxGroup parent, KdbxGroup group)
    {
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor == group) return true;
        }
        return false;
    }
}
