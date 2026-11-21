using KeePassModern.Core.Crypto;

namespace KeePassModern.Core.Kdbx;

public sealed class KdbxDatabase : IDisposable
{
    public uint FormatVersion { get; set; } = KdbxConstants.Version40;
    public KdbxCipherAlgorithm Cipher { get; set; } = KdbxCipherAlgorithm.Aes256;
    public KdbxKdfAlgorithm Kdf { get; set; } = KdbxKdfAlgorithm.Argon2id;
    public KdbxCompression Compression { get; set; } = KdbxCompression.GZip;

    // KDF parameters
    public ulong KdfIterations { get; set; } = 3;
    public ulong KdfMemoryBytes { get; set; } = 64UL * 1024 * 1024; // 64 MiB
    public uint KdfParallelism { get; set; } = 2;
    public ulong AesKdfRounds { get; set; } = 600_000;

    public KdbxMeta Meta { get; set; } = new();
    public KdbxGroup RootGroup { get; set; } = new() { Name = "Root", IconId = 48 };
    public List<KdbxDeletedObject> DeletedObjects { get; } = new();

    public string? FilePath { get; set; }
    public bool IsModified { get; set; }

    public IEnumerable<KdbxEntry> EnumerateAllEntries(bool includeRecycleBin = false)
    {
        foreach (var entry in EnumerateEntriesRecursive(RootGroup, includeRecycleBin))
            yield return entry;
    }

    public IEnumerable<KdbxGroup> EnumerateAllGroups(bool includeRecycleBin = true)
    {
        yield return RootGroup;
        foreach (var g in EnumerateGroupsRecursive(RootGroup, includeRecycleBin))
            yield return g;
    }

    public KdbxGroup? FindGroupByUuid(Guid uuid)
        => EnumerateAllGroups(true).FirstOrDefault(g => g.Uuid == uuid);

    public KdbxEntry? FindEntryByUuid(Guid uuid)
        => EnumerateAllEntries(true).FirstOrDefault(e => e.Uuid == uuid);

    public KdbxGroup GetOrCreateRecycleBin()
    {
        if (Meta.RecycleBinUuid != Guid.Empty)
        {
            var existing = FindGroupByUuid(Meta.RecycleBinUuid);
            if (existing is not null) return existing;
        }

        var bin = new KdbxGroup
        {
            Uuid = Guid.NewGuid(),
            Name = "回收站",
            IconId = 43,
            EnableAutoType = false,
            EnableSearching = false
        };
        RootGroup.AddGroup(bin);
        Meta.RecycleBinEnabled = true;
        Meta.RecycleBinUuid = bin.Uuid;
        Meta.RecycleBinChanged = DateTimeOffset.UtcNow;
        IsModified = true;
        return bin;
    }

    public bool IsInRecycleBin(KdbxGroup group)
    {
        if (!Meta.RecycleBinEnabled || Meta.RecycleBinUuid == Guid.Empty) return false;
        var cur = group;
        while (cur is not null)
        {
            if (cur.Uuid == Meta.RecycleBinUuid) return true;
            cur = cur.Parent;
        }
        return false;
    }

    public bool IsInRecycleBin(KdbxEntry entry)
        => entry.Parent is not null && IsInRecycleBin(entry.Parent);

    public void MoveEntryToRecycleBin(KdbxEntry entry)
    {
        if (entry.Parent is null) return;
        if (!Meta.RecycleBinEnabled || IsInRecycleBin(entry))
        {
            DeleteEntryPermanently(entry);
            return;
        }

        var bin = GetOrCreateRecycleBin();
        entry.PreviousParentGroup = entry.Parent.Uuid;
        entry.Parent.Entries.Remove(entry);
        bin.AddEntry(entry);
        entry.LocationChanged = DateTimeOffset.UtcNow;
        entry.LastModificationTime = DateTimeOffset.UtcNow;
        IsModified = true;
    }

    public bool RestoreEntryFromRecycleBin(KdbxEntry entry)
    {
        if (entry.Parent is null || !IsInRecycleBin(entry)) return false;
        var target = (entry.PreviousParentGroup != Guid.Empty ? FindGroupByUuid(entry.PreviousParentGroup) : null)
                     ?? RootGroup;
        if (IsInRecycleBin(target)) target = RootGroup;

        entry.Parent.Entries.Remove(entry);
        target.AddEntry(entry);
        entry.PreviousParentGroup = Guid.Empty;
        entry.LocationChanged = DateTimeOffset.UtcNow;
        entry.LastModificationTime = DateTimeOffset.UtcNow;
        IsModified = true;
        return true;
    }

    public void DeleteEntryPermanently(KdbxEntry entry)
    {
        entry.Parent?.Entries.Remove(entry);
        DeletedObjects.Add(new KdbxDeletedObject { Uuid = entry.Uuid, DeletionTime = DateTimeOffset.UtcNow });
        entry.Dispose();
        IsModified = true;
    }

    public void EmptyRecycleBin()
    {
        if (Meta.RecycleBinUuid == Guid.Empty) return;
        var bin = FindGroupByUuid(Meta.RecycleBinUuid);
        if (bin is null) return;
        foreach (var e in bin.Entries.ToList())
            DeleteEntryPermanently(e);
        foreach (var g in bin.Groups.ToList())
        {
            DeletedObjects.Add(new KdbxDeletedObject { Uuid = g.Uuid, DeletionTime = DateTimeOffset.UtcNow });
            bin.Groups.Remove(g);
        }
        IsModified = true;
    }

    /// <summary>
    /// Determines whether KDBX 4.1 is required (conditional 4.1 upgrade per KeePass 2.x specification).
    /// </summary>
    public bool RequiresKdbx41()
    {
        if (Meta.CustomIcons.Any(i => !string.IsNullOrEmpty(i.Name) || i.LastModificationTime.HasValue))
            return true;
        if (Meta.CustomData.Any(kv => kv.Value.LastModificationTime.HasValue))
            return true;

        foreach (var g in EnumerateAllGroups(true))
        {
            if (g.Tags.Count > 0 || g.PreviousParentGroup != Guid.Empty)
                return true;
        }
        foreach (var e in EnumerateAllEntries(true))
        {
            if (!e.QualityCheck || e.PreviousParentGroup != Guid.Empty)
                return true;
        }
        return false;
    }

    public static KdbxDatabase CreateDefault(string name = "个人密码库")
    {
        var db = new KdbxDatabase
        {
            Meta = new KdbxMeta
            {
                DatabaseName = name,
                DatabaseDescription = "Created with KeePassModern",
                Generator = "KeePassModern",
                RecycleBinEnabled = true,
                HistoryMaxItems = 10,
                HistoryMaxSize = 6 * 1024 * 1024
            },
            RootGroup = new KdbxGroup { Name = name, IconId = 48 }
        };

        db.RootGroup.AddGroup(new KdbxGroup { Name = "常用账号", IconId = 48 });
        db.RootGroup.AddGroup(new KdbxGroup { Name = "互联网与社交", IconId = 1 });
        db.RootGroup.AddGroup(new KdbxGroup { Name = "开发与云服务", IconId = 3 });
        db.RootGroup.AddGroup(new KdbxGroup { Name = "金融与银行", IconId = 37 });
        db.RootGroup.AddGroup(new KdbxGroup { Name = "电子邮箱", IconId = 19 });
        db.GetOrCreateRecycleBin();
        db.IsModified = false;
        return db;
    }

    private IEnumerable<KdbxEntry> EnumerateEntriesRecursive(KdbxGroup group, bool includeRecycleBin)
    {
        if (!includeRecycleBin && Meta.RecycleBinEnabled && group.Uuid == Meta.RecycleBinUuid)
            yield break;

        foreach (var entry in group.Entries)
            yield return entry;

        foreach (var sub in group.Groups)
        {
            foreach (var entry in EnumerateEntriesRecursive(sub, includeRecycleBin))
                yield return entry;
        }
    }

    private IEnumerable<KdbxGroup> EnumerateGroupsRecursive(KdbxGroup group, bool includeRecycleBin)
    {
        foreach (var sub in group.Groups)
        {
            if (!includeRecycleBin && Meta.RecycleBinEnabled && sub.Uuid == Meta.RecycleBinUuid)
                continue;
            yield return sub;
            foreach (var child in EnumerateGroupsRecursive(sub, includeRecycleBin))
                yield return child;
        }
    }

    public void Dispose()
    {
        RootGroup.Dispose();
    }
}

public sealed class KdbxMeta
{
    public string Generator { get; set; } = "KeePassModern";
    public string DatabaseName { get; set; } = "密码库";
    public DateTimeOffset DatabaseNameChanged { get; set; } = DateTimeOffset.UtcNow;
    public string DatabaseDescription { get; set; } = string.Empty;
    public DateTimeOffset DatabaseDescriptionChanged { get; set; } = DateTimeOffset.UtcNow;
    public string DefaultUserName { get; set; } = string.Empty;
    public DateTimeOffset DefaultUserNameChanged { get; set; } = DateTimeOffset.UtcNow;
    public int MaintenanceHistoryDays { get; set; } = 365;
    public string Color { get; set; } = string.Empty;
    public DateTimeOffset MasterKeyChanged { get; set; } = DateTimeOffset.UtcNow;
    public long MasterKeyChangeRec { get; set; } = -1;
    public long MasterKeyChangeForce { get; set; } = -1;
    public bool ProtectTitle { get; set; }
    public bool ProtectUserName { get; set; }
    public bool ProtectPassword { get; set; } = true;
    public bool ProtectUrl { get; set; }
    public bool ProtectNotes { get; set; }
    public bool RecycleBinEnabled { get; set; } = true;
    public Guid RecycleBinUuid { get; set; } = Guid.Empty;
    public DateTimeOffset RecycleBinChanged { get; set; } = DateTimeOffset.UtcNow;
    public Guid EntryTemplatesGroup { get; set; } = Guid.Empty;
    public DateTimeOffset EntryTemplatesGroupChanged { get; set; } = DateTimeOffset.UtcNow;
    public int HistoryMaxItems { get; set; } = 10;
    public long HistoryMaxSize { get; set; } = 6 * 1024 * 1024;
    public Guid LastSelectedGroup { get; set; } = Guid.Empty;
    public Guid LastTopVisibleGroup { get; set; } = Guid.Empty;
    public List<KdbxCustomIcon> CustomIcons { get; } = new();
    public Dictionary<string, KdbxCustomDataItem> CustomData { get; } = new();
}

public sealed class KdbxCustomIcon
{
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset? LastModificationTime { get; set; }
}

public sealed class KdbxCustomDataItem
{
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset? LastModificationTime { get; set; }
}

public sealed class KdbxGroup : IDisposable
{
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int IconId { get; set; } = 48;
    public Guid CustomIconUuid { get; set; } = Guid.Empty;
    public bool IsExpanded { get; set; } = true;
    public string DefaultAutoTypeSequence { get; set; } = string.Empty;
    public bool? EnableAutoType { get; set; }
    public bool? EnableSearching { get; set; }
    public Guid LastTopVisibleEntry { get; set; } = Guid.Empty;
    public Guid PreviousParentGroup { get; set; } = Guid.Empty;
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, KdbxCustomDataItem> CustomData { get; } = new();

    public DateTimeOffset CreationTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModificationTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAccessTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiryTime { get; set; } = DateTimeOffset.UtcNow;
    public bool Expires { get; set; }
    public int UsageCount { get; set; }
    public DateTimeOffset LocationChanged { get; set; } = DateTimeOffset.UtcNow;

    public KdbxGroup? Parent { get; internal set; }
    public List<KdbxGroup> Groups { get; } = new();
    public List<KdbxEntry> Entries { get; } = new();

    public void AddGroup(KdbxGroup group)
    {
        group.Parent = this;
        Groups.Add(group);
    }

    public void AddEntry(KdbxEntry entry)
    {
        entry.Parent = this;
        Entries.Add(entry);
    }

    public string GetFullPath(string separator = " / ")
    {
        var parts = new Stack<string>();
        var cur = this;
        while (cur is not null)
        {
            parts.Push(cur.Name);
            cur = cur.Parent;
        }
        return string.Join(separator, parts);
    }

    public void Dispose()
    {
        foreach (var e in Entries) e.Dispose();
        foreach (var g in Groups) g.Dispose();
    }
}

public sealed class KdbxEntry : IDisposable
{
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public int IconId { get; set; }
    public Guid CustomIconUuid { get; set; } = Guid.Empty;
    public string ForegroundColor { get; set; } = string.Empty;
    public string BackgroundColor { get; set; } = string.Empty;
    public string OverrideUrl { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public bool QualityCheck { get; set; } = true;
    public Guid PreviousParentGroup { get; set; } = Guid.Empty;

    public DateTimeOffset CreationTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModificationTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastAccessTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiryTime { get; set; } = DateTimeOffset.UtcNow;
    public bool Expires { get; set; }
    public int UsageCount { get; set; }
    public DateTimeOffset LocationChanged { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, ProtectedString> Strings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ProtectedBinary> Binaries { get; } = new(StringComparer.Ordinal);
    public KdbxAutoType AutoType { get; set; } = new();
    public Dictionary<string, KdbxCustomDataItem> CustomData { get; } = new();
    public List<KdbxEntry> History { get; } = new();

    public KdbxGroup? Parent { get; internal set; }

    public string Title
    {
        get => GetString(KdbxConstants.FieldTitle);
        set => SetString(KdbxConstants.FieldTitle, value, false);
    }

    public string UserName
    {
        get => GetString(KdbxConstants.FieldUserName);
        set => SetString(KdbxConstants.FieldUserName, value, false);
    }

    public string Password
    {
        get => GetString(KdbxConstants.FieldPassword);
        set => SetString(KdbxConstants.FieldPassword, value, true);
    }

    public string Url
    {
        get => GetString(KdbxConstants.FieldUrl);
        set => SetString(KdbxConstants.FieldUrl, value, false);
    }

    public string Notes
    {
        get => GetString(KdbxConstants.FieldNotes);
        set => SetString(KdbxConstants.FieldNotes, value, false);
    }

    public bool IsFavorite
    {
        get => Tags.Any(t => string.Equals(t, "Favorite", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(t, "收藏", StringComparison.OrdinalIgnoreCase));
        set
        {
            Tags.RemoveAll(t => string.Equals(t, "Favorite", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(t, "收藏", StringComparison.OrdinalIgnoreCase));
            if (value) Tags.Add("Favorite");
        }
    }

    public bool IsExpired => Expires && ExpiryTime <= DateTimeOffset.UtcNow;

    public string GetString(string key)
        => Strings.TryGetValue(key, out var ps) ? ps.ReadString() : string.Empty;

    public void SetString(string key, string value, bool isProtected)
    {
        if (Strings.TryGetValue(key, out var existing))
            existing.Dispose();
        Strings[key] = new ProtectedString(isProtected, value);
    }

    public IEnumerable<KeyValuePair<string, ProtectedString>> GetCustomStrings()
    {
        foreach (var kv in Strings)
        {
            if (kv.Key is KdbxConstants.FieldTitle or KdbxConstants.FieldUserName
                or KdbxConstants.FieldPassword or KdbxConstants.FieldUrl or KdbxConstants.FieldNotes)
                continue;
            yield return kv;
        }
    }

    public bool HasTotp()
        => !string.IsNullOrWhiteSpace(GetString(KdbxConstants.FieldOtp)) ||
           !string.IsNullOrWhiteSpace(GetString(KdbxConstants.FieldTimeOtpSecretBase32));

    public void CreateHistorySnapshot(int maxItems = 10)
    {
        var snapshot = CloneWithoutHistory();
        History.Add(snapshot);
        while (maxItems >= 0 && History.Count > maxItems)
        {
            History[0].Dispose();
            History.RemoveAt(0);
        }
    }

    public KdbxEntry CloneWithoutHistory()
    {
        var clone = new KdbxEntry
        {
            Uuid = Uuid,
            IconId = IconId,
            CustomIconUuid = CustomIconUuid,
            ForegroundColor = ForegroundColor,
            BackgroundColor = BackgroundColor,
            OverrideUrl = OverrideUrl,
            Tags = new List<string>(Tags),
            QualityCheck = QualityCheck,
            PreviousParentGroup = PreviousParentGroup,
            CreationTime = CreationTime,
            LastModificationTime = LastModificationTime,
            LastAccessTime = LastAccessTime,
            ExpiryTime = ExpiryTime,
            Expires = Expires,
            UsageCount = UsageCount,
            LocationChanged = LocationChanged,
            AutoType = AutoType.Clone()
        };

        foreach (var kv in Strings)
            clone.Strings[kv.Key] = kv.Value.Clone();
        foreach (var kv in Binaries)
            clone.Binaries[kv.Key] = kv.Value.Clone();
        foreach (var kv in CustomData)
            clone.CustomData[kv.Key] = new KdbxCustomDataItem
            {
                Value = kv.Value.Value,
                LastModificationTime = kv.Value.LastModificationTime
            };

        return clone;
    }

    public void RestoreFromHistory(KdbxEntry snapshot, int maxItems = 10)
    {
        CreateHistorySnapshot(maxItems);

        IconId = snapshot.IconId;
        CustomIconUuid = snapshot.CustomIconUuid;
        ForegroundColor = snapshot.ForegroundColor;
        BackgroundColor = snapshot.BackgroundColor;
        OverrideUrl = snapshot.OverrideUrl;
        Tags = new List<string>(snapshot.Tags);
        QualityCheck = snapshot.QualityCheck;
        Expires = snapshot.Expires;
        ExpiryTime = snapshot.ExpiryTime;
        AutoType = snapshot.AutoType.Clone();

        foreach (var ps in Strings.Values) ps.Dispose();
        Strings.Clear();
        foreach (var kv in snapshot.Strings)
            Strings[kv.Key] = kv.Value.Clone();

        foreach (var pb in Binaries.Values) pb.Dispose();
        Binaries.Clear();
        foreach (var kv in snapshot.Binaries)
            Binaries[kv.Key] = kv.Value.Clone();

        LastModificationTime = DateTimeOffset.UtcNow;
    }

    public void Dispose()
    {
        foreach (var ps in Strings.Values) ps.Dispose();
        Strings.Clear();
        foreach (var pb in Binaries.Values) pb.Dispose();
        Binaries.Clear();
        foreach (var h in History) h.Dispose();
        History.Clear();
    }
}

public sealed class KdbxAutoType
{
    public bool Enabled { get; set; } = true;
    public int DataTransferObfuscation { get; set; }
    public string DefaultSequence { get; set; } = string.Empty;
    public List<KdbxAutoTypeAssociation> Associations { get; } = new();

    public KdbxAutoType Clone()
    {
        var copy = new KdbxAutoType
        {
            Enabled = Enabled,
            DataTransferObfuscation = DataTransferObfuscation,
            DefaultSequence = DefaultSequence
        };
        foreach (var a in Associations)
            copy.Associations.Add(new KdbxAutoTypeAssociation { Window = a.Window, KeystrokeSequence = a.KeystrokeSequence });
        return copy;
    }
}

public sealed class KdbxAutoTypeAssociation
{
    public string Window { get; set; } = string.Empty;
    public string KeystrokeSequence { get; set; } = string.Empty;
}

public sealed class KdbxDeletedObject
{
    public Guid Uuid { get; set; }
    public DateTimeOffset DeletionTime { get; set; } = DateTimeOffset.UtcNow;
}
