using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KeeDesk.Core.Crypto;
using KeeDesk.Core.Services;

namespace KeeDesk.Core.Kdbx;

/// <summary>
/// Serializes and deserializes KeePass 2.x XML payload for both KDBX 3.1 (ISO8601 dates + Meta/Binaries)
/// and KDBX 4.0/4.1 (Base64 Int64 dates + Inner Header binary pool).
/// </summary>
public static class KdbxXmlSerializer
{
    private static readonly DateTimeOffset KdbxEpoch = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public sealed class InnerStreamCipher
    {
        private readonly KdbxInnerStreamAlgorithm _algo;
        private readonly ChaCha20Cipher? _chacha;
        private readonly Salsa20Cipher? _salsa;

        public InnerStreamCipher(KdbxInnerStreamAlgorithm algo, byte[] key)
        {
            _algo = algo;
            if (algo == KdbxInnerStreamAlgorithm.ChaCha20)
            {
                byte[] hash = SHA512.HashData(key);
                _chacha = new ChaCha20Cipher(hash.AsSpan(0, 32), hash.AsSpan(32, 12));
                CryptographicOperations.ZeroMemory(hash);
            }
            else if (algo == KdbxInnerStreamAlgorithm.Salsa20)
            {
                byte[] hash = SHA256.HashData(key);
                _salsa = new Salsa20Cipher(hash, KdbxConstants.InnerSalsa20Iv);
                CryptographicOperations.ZeroMemory(hash);
            }
        }

        public byte[] Process(ReadOnlySpan<byte> input)
        {
            if (input.IsEmpty || _algo == KdbxInnerStreamAlgorithm.None)
                return input.ToArray();
            if (_chacha is not null) return _chacha.ProcessBytes(input);
            if (_salsa is not null) return _salsa.ProcessBytes(input);
            return input.ToArray();
        }
    }

    public static void ReadXml(
        Stream xmlStream,
        KdbxDatabase db,
        bool isKdbx4,
        InnerStreamCipher? innerCipher,
        List<ProtectedBinary> binaryPool)
    {
        var doc = XDocument.Load(xmlStream, LoadOptions.PreserveWhitespace);
        var root = doc.Root ?? throw new InvalidDataException("Missing KeePassFile root element.");

        var metaEl = root.Element("Meta");
        if (metaEl is not null)
        {
            ReadMeta(metaEl, db.Meta, isKdbx4, binaryPool);
        }

        var rootEl = root.Element("Root");
        var groupEl = rootEl?.Element("Group");
        if (groupEl is not null)
        {
            db.RootGroup = ReadGroup(groupEl, isKdbx4, innerCipher, binaryPool);
        }

        db.DeletedObjects.Clear();
        var deletedEl = rootEl?.Element("DeletedObjects");
        if (deletedEl is not null)
        {
            foreach (var d in deletedEl.Elements("DeletedObject"))
            {
                db.DeletedObjects.Add(new KdbxDeletedObject
                {
                    Uuid = DecodeUuid(d.Element("UUID")?.Value),
                    DeletionTime = DecodeTime(d.Element("DeletionTime")?.Value, isKdbx4)
                });
            }
        }
    }

    public static byte[] WriteXml(
        KdbxDatabase db,
        bool isKdbx4,
        InnerStreamCipher? innerCipher,
        List<ProtectedBinary> binaryPool,
        byte[]? headerHashForKdbx3 = null)
    {
        // Collect all entry binaries into pool
        binaryPool.Clear();
        var binaryRefMap = new Dictionary<KdbxEntry, Dictionary<string, int>>();
        CollectBinaries(db.RootGroup, binaryPool, binaryRefMap);

        var root = new XElement("KeePassFile",
            WriteMeta(db.Meta, isKdbx4, binaryPool, headerHashForKdbx3),
            new XElement("Root",
                WriteGroup(db.RootGroup, isKdbx4, innerCipher, binaryRefMap),
                WriteDeletedObjects(db.DeletedObjects, isKdbx4)));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            OmitXmlDeclaration = false
        }))
        {
            doc.Save(writer);
        }
        return ms.ToArray();
    }

    internal static XDocument CreateSearchDocument(KdbxDatabase db, EntrySearchParameters parameters,
        HashSet<KdbxEntry> candidates)
    {
        var search = new SearchProjection(parameters, candidates);
        return new XDocument(new XElement("KeePassFile",
            new XElement("Root", WriteGroup(parameters.ScopeGroup ?? db.RootGroup, false, null, new(), search))));
    }

    private sealed record SearchProjection(EntrySearchParameters Parameters, HashSet<KdbxEntry> Candidates);

    private static void CollectBinaries(
        KdbxGroup group,
        List<ProtectedBinary> pool,
        Dictionary<KdbxEntry, Dictionary<string, int>> map)
    {
        foreach (var entry in group.Entries)
        {
            CollectEntryBinaries(entry, pool, map);
            foreach (var h in entry.History)
                CollectEntryBinaries(h, pool, map);
        }
        foreach (var sub in group.Groups)
            CollectBinaries(sub, pool, map);
    }

    private static void CollectEntryBinaries(
        KdbxEntry entry,
        List<ProtectedBinary> pool,
        Dictionary<KdbxEntry, Dictionary<string, int>> map)
    {
        if (entry.Binaries.Count == 0) return;
        var entryRefs = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, bin) in entry.Binaries)
        {
            int idx = pool.Count;
            pool.Add(bin);
            entryRefs[name] = idx;
        }
        map[entry] = entryRefs;
    }

    private static void ReadMeta(XElement el, KdbxMeta meta, bool isKdbx4, List<ProtectedBinary> binaryPool)
    {
        meta.Generator = el.Element("Generator")?.Value ?? "KeeDesk";
        meta.DatabaseName = el.Element("DatabaseName")?.Value ?? string.Empty;
        meta.DatabaseNameChanged = DecodeTime(el.Element("DatabaseNameChanged")?.Value, isKdbx4);
        meta.DatabaseDescription = el.Element("DatabaseDescription")?.Value ?? string.Empty;
        meta.DatabaseDescriptionChanged = DecodeTime(el.Element("DatabaseDescriptionChanged")?.Value, isKdbx4);
        meta.DefaultUserName = el.Element("DefaultUserName")?.Value ?? string.Empty;
        meta.DefaultUserNameChanged = DecodeTime(el.Element("DefaultUserNameChanged")?.Value, isKdbx4);
        meta.MaintenanceHistoryDays = ParseInt(el.Element("MaintenanceHistoryDays")?.Value, 365);
        meta.Color = el.Element("Color")?.Value ?? string.Empty;
        meta.MasterKeyChanged = DecodeTime(el.Element("MasterKeyChanged")?.Value, isKdbx4);
        meta.MasterKeyChangeRec = ParseLong(el.Element("MasterKeyChangeRec")?.Value, -1);
        meta.MasterKeyChangeForce = ParseLong(el.Element("MasterKeyChangeForce")?.Value, -1);

        var mp = el.Element("MemoryProtection");
        if (mp is not null)
        {
            meta.ProtectTitle = ParseBool(mp.Element("ProtectTitle")?.Value, false);
            meta.ProtectUserName = ParseBool(mp.Element("ProtectUserName")?.Value, false);
            meta.ProtectPassword = ParseBool(mp.Element("ProtectPassword")?.Value, true);
            meta.ProtectUrl = ParseBool(mp.Element("ProtectURL")?.Value, false);
            meta.ProtectNotes = ParseBool(mp.Element("ProtectNotes")?.Value, false);
        }

        meta.RecycleBinEnabled = ParseBool(el.Element("RecycleBinEnabled")?.Value, true);
        meta.RecycleBinUuid = DecodeUuid(el.Element("RecycleBinUUID")?.Value);
        meta.RecycleBinChanged = DecodeTime(el.Element("RecycleBinChanged")?.Value, isKdbx4);
        meta.EntryTemplatesGroup = DecodeUuid(el.Element("EntryTemplatesGroup")?.Value);
        meta.EntryTemplatesGroupChanged = DecodeTime(el.Element("EntryTemplatesGroupChanged")?.Value, isKdbx4);
        meta.HistoryMaxItems = ParseInt(el.Element("HistoryMaxItems")?.Value, 10);
        meta.HistoryMaxSize = ParseLong(el.Element("HistoryMaxSize")?.Value, 6 * 1024 * 1024);
        meta.LastSelectedGroup = DecodeUuid(el.Element("LastSelectedGroup")?.Value);
        meta.LastTopVisibleGroup = DecodeUuid(el.Element("LastTopVisibleGroup")?.Value);

        meta.CustomIcons.Clear();
        var iconsEl = el.Element("CustomIcons");
        if (iconsEl is not null)
        {
            foreach (var icon in iconsEl.Elements("Icon"))
            {
                var dataStr = icon.Element("Data")?.Value;
                var lmtStr = icon.Element("LastModificationTime")?.Value;
                meta.CustomIcons.Add(new KdbxCustomIcon
                {
                    Uuid = DecodeUuid(icon.Element("UUID")?.Value),
                    Data = string.IsNullOrEmpty(dataStr) ? Array.Empty<byte>() : Convert.FromBase64String(dataStr),
                    Name = icon.Element("Name")?.Value ?? string.Empty,
                    LastModificationTime = string.IsNullOrEmpty(lmtStr) ? null : DecodeTime(lmtStr, isKdbx4)
                });
            }
        }

        // KDBX 3.1 stores binaries in Meta/Binaries
        if (!isKdbx4)
        {
            var binsEl = el.Element("Binaries");
            if (binsEl is not null)
            {
                foreach (var b in binsEl.Elements("Binary"))
                {
                    int id = ParseInt(b.Attribute("ID")?.Value, binaryPool.Count);
                    bool compressed = ParseBool(b.Attribute("Compressed")?.Value, false);
                    byte[] raw = string.IsNullOrEmpty(b.Value) ? Array.Empty<byte>() : Convert.FromBase64String(b.Value);
                    if (compressed && raw.Length > 0)
                        raw = DecompressGZip(raw);
                    while (binaryPool.Count <= id)
                        binaryPool.Add(new ProtectedBinary(false, Array.Empty<byte>()));
                    binaryPool[id] = new ProtectedBinary(false, raw);
                }
            }
        }

        ReadCustomData(el.Element("CustomData"), meta.CustomData, isKdbx4);
    }

    private static XElement WriteMeta(KdbxMeta meta, bool isKdbx4, List<ProtectedBinary> binaryPool, byte[]? headerHashForKdbx3)
    {
        var el = new XElement("Meta",
            new XElement("Generator", meta.Generator));
        if (!isKdbx4 && headerHashForKdbx3 is not null)
            el.Add(new XElement("HeaderHash", Convert.ToBase64String(headerHashForKdbx3)));

        el.Add(
            new XElement("DatabaseName", meta.DatabaseName),
            new XElement("DatabaseNameChanged", EncodeTime(meta.DatabaseNameChanged, isKdbx4)),
            new XElement("DatabaseDescription", meta.DatabaseDescription),
            new XElement("DatabaseDescriptionChanged", EncodeTime(meta.DatabaseDescriptionChanged, isKdbx4)),
            new XElement("DefaultUserName", meta.DefaultUserName),
            new XElement("DefaultUserNameChanged", EncodeTime(meta.DefaultUserNameChanged, isKdbx4)),
            new XElement("MaintenanceHistoryDays", meta.MaintenanceHistoryDays),
            new XElement("Color", meta.Color),
            new XElement("MasterKeyChanged", EncodeTime(meta.MasterKeyChanged, isKdbx4)),
            new XElement("MasterKeyChangeRec", meta.MasterKeyChangeRec),
            new XElement("MasterKeyChangeForce", meta.MasterKeyChangeForce),
            new XElement("MemoryProtection",
                new XElement("ProtectTitle", meta.ProtectTitle ? "True" : "False"),
                new XElement("ProtectUserName", meta.ProtectUserName ? "True" : "False"),
                new XElement("ProtectPassword", meta.ProtectPassword ? "True" : "False"),
                new XElement("ProtectURL", meta.ProtectUrl ? "True" : "False"),
                new XElement("ProtectNotes", meta.ProtectNotes ? "True" : "False")),
            new XElement("RecycleBinEnabled", meta.RecycleBinEnabled ? "True" : "False"),
            new XElement("RecycleBinUUID", EncodeUuid(meta.RecycleBinUuid)),
            new XElement("RecycleBinChanged", EncodeTime(meta.RecycleBinChanged, isKdbx4)),
            new XElement("EntryTemplatesGroup", EncodeUuid(meta.EntryTemplatesGroup)),
            new XElement("EntryTemplatesGroupChanged", EncodeTime(meta.EntryTemplatesGroupChanged, isKdbx4)),
            new XElement("HistoryMaxItems", meta.HistoryMaxItems),
            new XElement("HistoryMaxSize", meta.HistoryMaxSize),
            new XElement("LastSelectedGroup", EncodeUuid(meta.LastSelectedGroup)),
            new XElement("LastTopVisibleGroup", EncodeUuid(meta.LastTopVisibleGroup)));

        if (meta.CustomIcons.Count > 0)
        {
            var iconsEl = new XElement("CustomIcons");
            foreach (var icon in meta.CustomIcons)
            {
                var iEl = new XElement("Icon",
                    new XElement("UUID", EncodeUuid(icon.Uuid)),
                    new XElement("Data", Convert.ToBase64String(icon.Data)));
                if (!string.IsNullOrEmpty(icon.Name))
                    iEl.Add(new XElement("Name", icon.Name));
                if (icon.LastModificationTime.HasValue)
                    iEl.Add(new XElement("LastModificationTime", EncodeTime(icon.LastModificationTime.Value, isKdbx4)));
                iconsEl.Add(iEl);
            }
            el.Add(iconsEl);
        }

        if (!isKdbx4 && binaryPool.Count > 0)
        {
            var binsEl = new XElement("Binaries");
            for (int i = 0; i < binaryPool.Count; i++)
            {
                var raw = binaryPool[i].ReadData();
                var compressed = CompressGZip(raw);
                binsEl.Add(new XElement("Binary",
                    new XAttribute("ID", i),
                    new XAttribute("Compressed", "True"),
                    Convert.ToBase64String(compressed)));
            }
            el.Add(binsEl);
        }

        if (meta.CustomData.Count > 0)
            el.Add(WriteCustomData(meta.CustomData, isKdbx4));

        return el;
    }

    private static KdbxGroup ReadGroup(XElement el, bool isKdbx4, InnerStreamCipher? cipher, List<ProtectedBinary> binaryPool)
    {
        var g = new KdbxGroup
        {
            Uuid = DecodeUuid(el.Element("UUID")?.Value),
            Name = el.Element("Name")?.Value ?? string.Empty,
            Notes = el.Element("Notes")?.Value ?? string.Empty,
            IconId = ParseInt(el.Element("IconID")?.Value, 48),
            CustomIconUuid = DecodeUuid(el.Element("CustomIconUUID")?.Value),
            IsExpanded = ParseBool(el.Element("IsExpanded")?.Value, true),
            DefaultAutoTypeSequence = el.Element("DefaultAutoTypeSequence")?.Value ?? string.Empty,
            EnableAutoType = ParseNullableBool(el.Element("EnableAutoType")?.Value),
            EnableSearching = ParseNullableBool(el.Element("EnableSearching")?.Value),
            LastTopVisibleEntry = DecodeUuid(el.Element("LastTopVisibleEntry")?.Value),
            PreviousParentGroup = DecodeUuid(el.Element("PreviousParentGroup")?.Value),
            Tags = ParseTags(el.Element("Tags")?.Value)
        };
        if (g.Uuid == Guid.Empty) g.Uuid = Guid.NewGuid();

        var times = el.Element("Times");
        if (times is not null)
        {
            g.CreationTime = DecodeTime(times.Element("CreationTime")?.Value, isKdbx4);
            g.LastModificationTime = DecodeTime(times.Element("LastModificationTime")?.Value, isKdbx4);
            g.LastAccessTime = DecodeTime(times.Element("LastAccessTime")?.Value, isKdbx4);
            g.ExpiryTime = DecodeTime(times.Element("ExpiryTime")?.Value, isKdbx4);
            g.Expires = ParseBool(times.Element("Expires")?.Value, false);
            g.UsageCount = ParseInt(times.Element("UsageCount")?.Value, 0);
            g.LocationChanged = DecodeTime(times.Element("LocationChanged")?.Value, isKdbx4);
        }

        ReadCustomData(el.Element("CustomData"), g.CustomData, isKdbx4);

        foreach (var entryEl in el.Elements("Entry"))
            g.AddEntry(ReadEntry(entryEl, isKdbx4, cipher, binaryPool));

        foreach (var subEl in el.Elements("Group"))
            g.AddGroup(ReadGroup(subEl, isKdbx4, cipher, binaryPool));

        return g;
    }

    private static XElement WriteGroup(
        KdbxGroup g,
        bool isKdbx4,
        InnerStreamCipher? cipher,
        Dictionary<KdbxEntry, Dictionary<string, int>> binaryRefMap,
        SearchProjection? search = null)
    {
        var el = new XElement("Group",
            new XElement("UUID", EncodeUuid(g.Uuid)),
            new XElement("Name", g.Name),
            new XElement("Notes", g.Notes),
            new XElement("IconID", g.IconId));

        if (g.CustomIconUuid != Guid.Empty)
            el.Add(new XElement("CustomIconUUID", EncodeUuid(g.CustomIconUuid)));

        el.Add(
            new XElement("Times",
                new XElement("CreationTime", EncodeTime(g.CreationTime, isKdbx4)),
                new XElement("LastModificationTime", EncodeTime(g.LastModificationTime, isKdbx4)),
                new XElement("LastAccessTime", EncodeTime(g.LastAccessTime, isKdbx4)),
                new XElement("ExpiryTime", EncodeTime(g.ExpiryTime, isKdbx4)),
                new XElement("Expires", g.Expires ? "True" : "False"),
                new XElement("UsageCount", g.UsageCount),
                new XElement("LocationChanged", EncodeTime(g.LocationChanged, isKdbx4))),
            new XElement("IsExpanded", g.IsExpanded ? "True" : "False"),
            new XElement("DefaultAutoTypeSequence", g.DefaultAutoTypeSequence),
            new XElement("EnableAutoType", FormatNullableBool(g.EnableAutoType)),
            new XElement("EnableSearching", FormatNullableBool(g.EnableSearching)),
            new XElement("LastTopVisibleEntry", EncodeUuid(g.LastTopVisibleEntry)));

        if (g.PreviousParentGroup != Guid.Empty)
            el.Add(new XElement("PreviousParentGroup", EncodeUuid(g.PreviousParentGroup)));
        if (g.Tags.Count > 0 && search?.Parameters.SearchTags != false)
            el.Add(new XElement("Tags", string.Join(";", g.Tags)));
        if (g.CustomData.Count > 0)
            el.Add(WriteCustomData(g.CustomData, isKdbx4));

        foreach (var entry in g.Entries)
        {
            if (search is not null && !search.Candidates.Contains(entry)) continue;
            var entryElement = WriteEntry(entry, isKdbx4, cipher, binaryRefMap,
                includeHistory: search?.Parameters.SearchHistory ?? true, search?.Parameters);
            if (search is not null)
                entryElement.AddAnnotation(entry);
            el.Add(entryElement);
        }

        foreach (var sub in g.Groups)
            el.Add(WriteGroup(sub, isKdbx4, cipher, binaryRefMap, search));

        return el;
    }

    private static KdbxEntry ReadEntry(XElement el, bool isKdbx4, InnerStreamCipher? cipher, List<ProtectedBinary> binaryPool)
    {
        var e = new KdbxEntry
        {
            Uuid = DecodeUuid(el.Element("UUID")?.Value),
            IconId = ParseInt(el.Element("IconID")?.Value, 0),
            CustomIconUuid = DecodeUuid(el.Element("CustomIconUUID")?.Value),
            ForegroundColor = el.Element("ForegroundColor")?.Value ?? string.Empty,
            BackgroundColor = el.Element("BackgroundColor")?.Value ?? string.Empty,
            OverrideUrl = el.Element("OverrideURL")?.Value ?? string.Empty,
            Tags = ParseTags(el.Element("Tags")?.Value),
            QualityCheck = ParseBool(el.Element("QualityCheck")?.Value, true),
            PreviousParentGroup = DecodeUuid(el.Element("PreviousParentGroup")?.Value)
        };
        if (e.Uuid == Guid.Empty) e.Uuid = Guid.NewGuid();

        var times = el.Element("Times");
        if (times is not null)
        {
            e.CreationTime = DecodeTime(times.Element("CreationTime")?.Value, isKdbx4);
            e.LastModificationTime = DecodeTime(times.Element("LastModificationTime")?.Value, isKdbx4);
            e.LastAccessTime = DecodeTime(times.Element("LastAccessTime")?.Value, isKdbx4);
            e.ExpiryTime = DecodeTime(times.Element("ExpiryTime")?.Value, isKdbx4);
            e.Expires = ParseBool(times.Element("Expires")?.Value, false);
            e.UsageCount = ParseInt(times.Element("UsageCount")?.Value, 0);
            e.LocationChanged = DecodeTime(times.Element("LocationChanged")?.Value, isKdbx4);
        }

        foreach (var strEl in el.Elements("String"))
        {
            var key = strEl.Element("Key")?.Value;
            if (string.IsNullOrEmpty(key)) continue;
            var valEl = strEl.Element("Value");
            bool isProtected = ParseBool(valEl?.Attribute("Protected")?.Value, false);
            string rawVal = valEl?.Value ?? string.Empty;

            if (isProtected && cipher is not null && !string.IsNullOrEmpty(rawVal))
            {
                byte[] enc = Convert.FromBase64String(rawVal);
                byte[] plain = cipher.Process(enc);
                e.Strings[key] = new ProtectedString(true, plain);
                CryptographicOperations.ZeroMemory(plain);
            }
            else
            {
                e.Strings[key] = new ProtectedString(isProtected, rawVal);
            }
        }

        foreach (var binEl in el.Elements("Binary"))
        {
            var key = binEl.Element("Key")?.Value;
            if (string.IsNullOrEmpty(key)) continue;
            var valEl = binEl.Element("Value");
            var refAttr = valEl?.Attribute("Ref")?.Value;
            if (int.TryParse(refAttr, out int idx) && idx >= 0 && idx < binaryPool.Count)
            {
                e.Binaries[key] = binaryPool[idx].Clone();
            }
        }

        var autoEl = el.Element("AutoType");
        if (autoEl is not null)
        {
            e.AutoType.Enabled = ParseBool(autoEl.Element("Enabled")?.Value, true);
            e.AutoType.DataTransferObfuscation = ParseInt(autoEl.Element("DataTransferObfuscation")?.Value, 0);
            e.AutoType.DefaultSequence = autoEl.Element("DefaultSequence")?.Value ?? string.Empty;
            foreach (var assoc in autoEl.Elements("Association"))
            {
                e.AutoType.Associations.Add(new KdbxAutoTypeAssociation
                {
                    Window = assoc.Element("Window")?.Value ?? string.Empty,
                    KeystrokeSequence = assoc.Element("KeystrokeSequence")?.Value ?? string.Empty
                });
            }
        }

        ReadCustomData(el.Element("CustomData"), e.CustomData, isKdbx4);

        var histEl = el.Element("History");
        if (histEl is not null)
        {
            foreach (var hEl in histEl.Elements("Entry"))
                e.History.Add(ReadEntry(hEl, isKdbx4, cipher, binaryPool));
        }

        return e;
    }

    private static XElement WriteEntry(
        KdbxEntry e,
        bool isKdbx4,
        InnerStreamCipher? cipher,
        Dictionary<KdbxEntry, Dictionary<string, int>> binaryRefMap,
        bool includeHistory,
        EntrySearchParameters? search = null)
    {
        var el = new XElement("Entry",
            new XElement("UUID", EncodeUuid(e.Uuid)),
            new XElement("IconID", e.IconId));

        if (e.CustomIconUuid != Guid.Empty)
            el.Add(new XElement("CustomIconUUID", EncodeUuid(e.CustomIconUuid)));

        el.Add(
            new XElement("ForegroundColor", e.ForegroundColor),
            new XElement("BackgroundColor", e.BackgroundColor),
            new XElement("OverrideURL", e.OverrideUrl),
            new XElement("Tags", search?.SearchTags == false ? string.Empty : string.Join(";", e.Tags)));

        if (!e.QualityCheck)
            el.Add(new XElement("QualityCheck", "False"));
        if (e.PreviousParentGroup != Guid.Empty)
            el.Add(new XElement("PreviousParentGroup", EncodeUuid(e.PreviousParentGroup)));

        el.Add(new XElement("Times",
            new XElement("CreationTime", EncodeTime(e.CreationTime, isKdbx4)),
            new XElement("LastModificationTime", EncodeTime(e.LastModificationTime, isKdbx4)),
            new XElement("LastAccessTime", EncodeTime(e.LastAccessTime, isKdbx4)),
            new XElement("ExpiryTime", EncodeTime(e.ExpiryTime, isKdbx4)),
            new XElement("Expires", e.Expires ? "True" : "False"),
            new XElement("UsageCount", e.UsageCount),
            new XElement("LocationChanged", EncodeTime(e.LocationChanged, isKdbx4))));

        foreach (var (key, ps) in e.Strings)
        {
            if (search?.SearchesString(key) == false)
            {
                el.Add(new XElement("String", new XElement("Key", key), new XElement("Value")));
            }
            else if (ps.IsProtected && cipher is not null)
            {
                byte[] utf8 = ps.ReadUtf8();
                byte[] enc = cipher.Process(utf8);
                CryptographicOperations.ZeroMemory(utf8);
                el.Add(new XElement("String",
                    new XElement("Key", key),
                    new XElement("Value", new XAttribute("Protected", "True"), Convert.ToBase64String(enc))));
            }
            else
            {
                el.Add(new XElement("String",
                    new XElement("Key", key),
                    new XElement("Value", ps.ReadString())));
            }
        }

        if (search is not null)
        {
            foreach (var name in e.Binaries.Keys)
                el.Add(new XElement("Binary", new XElement("Key", name), new XElement("Value")));
        }
        else if (binaryRefMap.TryGetValue(e, out var refs))
        {
            foreach (var (name, idx) in refs)
            {
                el.Add(new XElement("Binary",
                    new XElement("Key", name),
                    new XElement("Value", new XAttribute("Ref", idx))));
            }
        }

        var autoEl = new XElement("AutoType",
            new XElement("Enabled", e.AutoType.Enabled ? "True" : "False"),
            new XElement("DataTransferObfuscation", e.AutoType.DataTransferObfuscation));
        if (!string.IsNullOrEmpty(e.AutoType.DefaultSequence))
            autoEl.Add(new XElement("DefaultSequence", e.AutoType.DefaultSequence));
        foreach (var a in e.AutoType.Associations)
        {
            autoEl.Add(new XElement("Association",
                new XElement("Window", a.Window),
                new XElement("KeystrokeSequence", a.KeystrokeSequence)));
        }
        el.Add(autoEl);

        if (e.CustomData.Count > 0)
            el.Add(WriteCustomData(e.CustomData, isKdbx4));

        if (includeHistory && e.History.Count > 0)
        {
            var histEl = new XElement("History");
            foreach (var h in e.History)
                histEl.Add(WriteEntry(h, isKdbx4, cipher, binaryRefMap, includeHistory: false, search));
            el.Add(histEl);
        }

        return el;
    }

    private static XElement WriteDeletedObjects(List<KdbxDeletedObject> list, bool isKdbx4)
    {
        var el = new XElement("DeletedObjects");
        foreach (var d in list)
        {
            el.Add(new XElement("DeletedObject",
                new XElement("UUID", EncodeUuid(d.Uuid)),
                new XElement("DeletionTime", EncodeTime(d.DeletionTime, isKdbx4))));
        }
        return el;
    }

    private static void ReadCustomData(XElement? el, Dictionary<string, KdbxCustomDataItem> target, bool isKdbx4)
    {
        if (el is null) return;
        foreach (var item in el.Elements("Item"))
        {
            var key = item.Element("Key")?.Value;
            if (string.IsNullOrEmpty(key)) continue;
            var lmtStr = item.Element("LastModificationTime")?.Value;
            target[key] = new KdbxCustomDataItem
            {
                Value = item.Element("Value")?.Value ?? string.Empty,
                LastModificationTime = string.IsNullOrEmpty(lmtStr) ? null : DecodeTime(lmtStr, isKdbx4)
            };
        }
    }

    private static XElement WriteCustomData(Dictionary<string, KdbxCustomDataItem> data, bool isKdbx4)
    {
        var el = new XElement("CustomData");
        foreach (var (key, item) in data)
        {
            var iEl = new XElement("Item",
                new XElement("Key", key),
                new XElement("Value", item.Value));
            if (item.LastModificationTime.HasValue)
                iEl.Add(new XElement("LastModificationTime", EncodeTime(item.LastModificationTime.Value, isKdbx4)));
            el.Add(iEl);
        }
        return el;
    }

    private static Guid DecodeUuid(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return Guid.Empty;
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            return bytes.Length == 16 ? new Guid(bytes) : Guid.Empty;
        }
        catch { return Guid.Empty; }
    }

    private static string EncodeUuid(Guid uuid) => Convert.ToBase64String(uuid.ToByteArray());

    private static DateTimeOffset DecodeTime(string? value, bool isKdbx4)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.UtcNow;
        value = value.Trim();

        if (isKdbx4 && !value.Contains('-'))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                if (bytes.Length == 8)
                {
                    long seconds = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                    return KdbxEpoch.AddSeconds(seconds);
                }
            }
            catch { }
        }

        if (DateTimeOffset.TryParse(value, out var dto))
            return dto.ToUniversalTime();
        return DateTimeOffset.UtcNow;
    }

    private static string EncodeTime(DateTimeOffset time, bool isKdbx4)
    {
        var utc = time.ToUniversalTime();
        if (isKdbx4)
        {
            long seconds = (long)(utc - KdbxEpoch).TotalSeconds;
            Span<byte> buf = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(buf, seconds);
            return Convert.ToBase64String(buf);
        }
        return utc.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private static List<string> ParseTags(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new List<string>();
        return value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ParseBool(string? v, bool def)
        => string.IsNullOrWhiteSpace(v) ? def : string.Equals(v.Trim(), "True", StringComparison.OrdinalIgnoreCase);

    private static bool? ParseNullableBool(string? v)
    {
        if (string.IsNullOrWhiteSpace(v) || string.Equals(v.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            return null;
        return string.Equals(v.Trim(), "True", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatNullableBool(bool? v) => v.HasValue ? (v.Value ? "True" : "False") : "null";

    private static int ParseInt(string? v, int def) => int.TryParse(v, out int r) ? r : def;
    private static long ParseLong(string? v, long def) => long.TryParse(v, out long r) ? r : def;

    internal static byte[] CompressGZip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    internal static byte[] DecompressGZip(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gz.CopyTo(output);
        return output.ToArray();
    }
}
