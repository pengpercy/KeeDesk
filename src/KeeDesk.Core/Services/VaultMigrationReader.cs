using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using KeeDesk.Core.Crypto;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public static class VaultMigrationReader
{
    private const int MaxJsonBytes = 64 * 1024 * 1024;
    private const long MaxAttachmentBytes = 128L * 1024 * 1024;
    private static readonly HashSet<string> StandardFields =
    [
        KdbxConstants.FieldTitle, KdbxConstants.FieldUserName, KdbxConstants.FieldPassword,
        KdbxConstants.FieldUrl, KdbxConstants.FieldNotes, KdbxConstants.FieldOtp
    ];

    public static KdbxGroup ReadBitwarden(Stream input, CancellationToken cancellation = default)
    {
        using var document = ReadJson(input, cancellation);
        var root = document.RootElement;
        if (Property(root, "encrypted").ValueKind == JsonValueKind.True)
            throw new EncryptedVaultMigrationException();

        var items = RequiredArray(root, "items");
        var group = new KdbxGroup { Name = "Bitwarden" };
        try
        {
            var folders = ReadNamedGroups(root, "folders", group);
            var collections = ReadNamedGroups(root, "collections", group);
            foreach (var item in items)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Property(item, "type").ValueKind != JsonValueKind.Number)
                    throw new InvalidDataException("Missing Bitwarden item type.");

                var target = folders.GetValueOrDefault(Value(item, "folderId")) ?? group;
                var names = Array(item, "collectionIds").Select(StringValue)
                    .Where(collections.ContainsKey).Distinct(StringComparer.Ordinal).ToArray();
                if (target == group && names.Length > 0)
                    target = collections[names[0]];

                var entry = new KdbxEntry { Title = Value(item, "name"), Notes = Value(item, "notes") };
                target.AddEntry(entry);
                entry.IsFavorite = Property(item, "favorite").ValueKind == JsonValueKind.True;
                if (DateTimeOffset.TryParse(Value(item, "creationDate"), out var created)) entry.CreationTime = created;
                if (DateTimeOffset.TryParse(Value(item, "revisionDate"), out var updated)) entry.LastModificationTime = updated;

                var login = Property(item, "login");
                entry.UserName = Value(login, "username");
                entry.Password = Value(login, "password");
                AddOtp(entry, Value(login, "totp"));
                AddUrls(entry, Array(login, "uris").Select(uri => Value(uri, "uri")));
                foreach (var field in Array(item, "fields"))
                    AddField(entry, Value(field, "name"), Value(field, "value"));
                foreach (string section in new[] { "card", "identity", "sshKey" })
                {
                    if (Property(item, section) is { ValueKind: JsonValueKind.Object } fields)
                        foreach (var field in fields.EnumerateObject())
                            AddField(entry, field.Name, StringValue(field.Value));
                }
                if (names.Length > 0)
                    AddField(entry, "Bitwarden collections", string.Join("\n", names.Select(id => collections[id].Name)));
                AddField(entry, "Bitwarden archived at", Value(item, "archivedDate"));
                AddField(entry, "Bitwarden deleted at", Value(item, "deletedDate"));
                AddArrayField(entry, "Bitwarden passkeys (JSON)", login, "fido2Credentials");
                AddArrayField(entry, "Bitwarden password history (JSON)", item, "passwordHistory");
                if (!Property(item, "type").TryGetInt32(out int type) || type is < 1 or > 5)
                    AddField(entry, "Bitwarden item (JSON)", CompactJson(item));
            }
            return group;
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }

    public static KdbxGroup ReadOnePassword(Stream input, CancellationToken cancellation = default)
    {
        if (!input.CanSeek || input.Length - input.Position > MaxAttachmentBytes + MaxJsonBytes)
            throw new InvalidDataException("The 1PUX archive exceeds its size limit or cannot be read.");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        using var data = (archive.GetEntry("export.data") ?? throw new InvalidDataException("Missing 1PUX export.data.")).Open();
        using var document = ReadJson(data, cancellation);
        var group = new KdbxGroup { Name = "1Password" };
        long attachmentBytes = 0;
        try
        {
            foreach (var account in RequiredArray(document.RootElement, "accounts"))
            {
                cancellation.ThrowIfCancellationRequested();
                var accountName = FirstNonEmpty(Value(Property(account, "attrs"), "accountName"),
                    Value(Property(account, "attrs"), "email"));
                var accountGroup = new KdbxGroup { Name = accountName.Length == 0 ? "1Password" : accountName };
                group.AddGroup(accountGroup);
                foreach (var vault in RequiredArray(account, "vaults"))
                {
                    var vaultGroup = new KdbxGroup { Name = Value(Property(vault, "attrs"), "name") };
                    accountGroup.AddGroup(vaultGroup);
                    foreach (var item in RequiredArray(vault, "items"))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var overview = Property(item, "overview");
                        var details = Property(item, "details");
                        if (overview.ValueKind != JsonValueKind.Object || details.ValueKind != JsonValueKind.Object)
                            throw new InvalidDataException("Missing 1PUX item data.");

                        var entry = new KdbxEntry { Title = Value(overview, "title"), Notes = Value(details, "notesPlain") };
                        vaultGroup.AddEntry(entry);
                        entry.Tags.AddRange(Array(overview, "tags").Select(StringValue)
                            .Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase));
                        if (Property(item, "favIndex") is { ValueKind: JsonValueKind.Number } favorite &&
                            favorite.TryGetInt32(out int index) && index > 0)
                            entry.IsFavorite = true;
                        if (ReadUnixTime(item, "createdAt") is { } created) entry.CreationTime = created;
                        if (ReadUnixTime(item, "updatedAt") is { } updated) entry.LastModificationTime = updated;

                        AddUrls(entry, Array(overview, "urls").Select(url => Value(url, "url")).Prepend(Value(overview, "url")));
                        foreach (var field in Array(details, "loginFields"))
                            SetLoginField(entry, Value(field, "designation"), Value(field, "name"), Value(field, "value"));

                        if (entry.Password.Length == 0) entry.Password = Value(details, "password");

                        foreach (var section in Array(details, "sections"))
                            foreach (var field in Array(section, "fields"))
                                ReadOnePasswordField(entry, field, archive, ref attachmentBytes);

                        AddAttachment(entry, archive, Property(details, "documentAttributes"), ref attachmentBytes);
                        var documentFile = Property(item, "file");
                        if (entry.Binaries.Count == 0 && Value(documentFile, "path") is { Length: > 0 } path)
                            AddArchiveEntry(entry, archive, path, Value(Property(documentFile, "attrs"), "fileName"), ref attachmentBytes);
                        AddField(entry, "1Password state", Value(item, "state") is "active" ? "" : Value(item, "state"));
                        AddField(entry, "1Password category", Value(item, "categoryUuid"));
                        AddField(entry, "1Password subtitle", Value(overview, "subtitle"));
                        AddArrayField(entry, "1Password password history (JSON)", details, "passwordHistory");
                    }
                }
            }
            return group;
        }
        catch
        {
            group.Dispose();
            throw;
        }
    }

    private static void ReadOnePasswordField(KdbxEntry entry, JsonElement field, ZipArchive archive, ref long attachmentBytes)
    {
        var value = Property(field, "value");
        if (value.ValueKind != JsonValueKind.Object) return;
        foreach (var typed in value.EnumerateObject())
        {
            string name = FirstNonEmpty(Value(field, "title"), Value(field, "id"), typed.Name);
            string content = StringValue(typed.Value);
            switch (typed.Name)
            {
                case "file":
                    AddAttachment(entry, archive, typed.Value, ref attachmentBytes);
                    break;
                case "totp":
                    AddOtp(entry, content);
                    break;
                default:
                    if (Value(field, "id") == "password" && entry.Password.Length == 0)
                        entry.Password = content;
                    else if (Value(field, "id") == "username" && entry.UserName.Length == 0)
                        entry.UserName = content;
                    else
                        AddField(entry, name, content);
                    break;
            }
        }
    }

    private static void AddAttachment(KdbxEntry entry, ZipArchive archive, JsonElement file, ref long bytes)
    {
        if (file.ValueKind != JsonValueKind.Object) return;
        var id = Value(file, "documentId");
        var name = Value(file, "fileName");
        if (id.Length == 0 || name.Length == 0) throw new InvalidDataException("Invalid 1PUX attachment reference.");
        var standard = $"files/{id}__{name}";
        var path = archive.GetEntry(standard) is null ? $"files/{id}___{name}" : standard;
        AddArchiveEntry(entry, archive, path, name, ref bytes);
    }

    private static void AddArchiveEntry(KdbxEntry entry, ZipArchive archive, string path, string name, ref long bytes)
    {
        if (!path.StartsWith("files/", StringComparison.Ordinal) || path.EndsWith('/'))
            throw new InvalidDataException("Invalid 1PUX attachment path.");
        var item = archive.GetEntry(path) ?? throw new InvalidDataException("A 1PUX attachment is missing.");
        if (item.Length < 0 || item.Length > MaxAttachmentBytes - bytes)
            throw new InvalidDataException("The 1PUX attachment limit was exceeded.");

        bytes += item.Length;
        var data = new byte[(int)item.Length];
        try
        {
            using var stream = item.Open();
            stream.ReadExactly(data);
            if (stream.ReadByte() != -1) throw new InvalidDataException("Unexpected 1PUX attachment length.");
            name = FirstNonEmpty(name, path[(path.LastIndexOf('/') + 1)..]);
            entry.Binaries.Add(UniqueName(name, entry.Binaries.ContainsKey), new ProtectedBinary(true, data));
        }
        finally { CryptographicOperations.ZeroMemory(data); }
    }

    private static void SetLoginField(KdbxEntry entry, string designation, string name, string value)
    {
        if (designation == "username" && entry.UserName.Length == 0) entry.UserName = value;
        else if (designation == "password" && entry.Password.Length == 0) entry.Password = value;
        else AddField(entry, name, value);
    }

    private static void AddOtp(KdbxEntry entry, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (entry.HasTotp()) AddField(entry, "OTP", value);
        else entry.SetString(KdbxConstants.FieldOtp, value, true);
    }

    private static void AddUrls(KdbxEntry entry, IEnumerable<string> urls)
    {
        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.Ordinal))
        {
            if (entry.Url.Length == 0) entry.Url = url;
            else AddField(entry, "URL", url);
        }
    }

    private static void AddArrayField(KdbxEntry entry, string name, JsonElement owner, string key)
    {
        if (Property(owner, key) is { ValueKind: JsonValueKind.Array } value && value.GetArrayLength() > 0)
            AddField(entry, name, CompactJson(value));
    }

    private static void AddField(KdbxEntry entry, string name, string value)
    {
        if (value.Length == 0) return;
        name = name.Length == 0 ? "Field" : name.Replace('=', '＝').Replace('\r', ' ').Replace('\n', ' ');
        entry.SetString(UniqueName(name, key => StandardFields.Contains(key) || entry.Strings.ContainsKey(key)), value, true);
    }

    private static string UniqueName(string name, Func<string, bool> exists)
    {
        var candidate = name;
        for (int suffix = 2; exists(candidate); suffix++)
            candidate = $"{name} ({suffix})";
        return candidate;
    }

    private static Dictionary<string, KdbxGroup> ReadNamedGroups(JsonElement root, string key, KdbxGroup parent)
    {
        var byId = new Dictionary<string, KdbxGroup>(StringComparer.Ordinal);
        foreach (var item in Array(root, key))
        {
            var id = Value(item, "id");
            var name = Value(item, "name");
            if (id.Length == 0 || byId.ContainsKey(id)) throw new InvalidDataException("Invalid migration group ID.");
            var target = parent;
            foreach (var part in name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var child = target.Groups.FirstOrDefault(group => group.Name == part);
                if (child is null)
                    target.AddGroup(child = new KdbxGroup { Name = part });
                target = child;
            }
            byId.Add(id, target);
        }
        return byId;
    }

    private static JsonDocument ReadJson(Stream input, CancellationToken cancellation)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                cancellation.ThrowIfCancellationRequested();
                if (bytes.Length + read > MaxJsonBytes) throw new InvalidDataException("The migration JSON is too large.");
                bytes.Write(buffer, 0, read);
            }
            var content = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
            if (content.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) content = content[3..];
            var reader = new Utf8JsonReader(content);
            var document = JsonDocument.ParseValue(ref reader);
            try
            {
                if (reader.Read()) throw new JsonException("Unexpected data after the migration document.");
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes.GetBuffer());
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static DateTimeOffset? ReadUnixTime(JsonElement owner, string key) =>
        Property(owner, key) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out long time)
            && time is >= -62135596800 and <= 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(time) : null;

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => value.Length > 0) ?? string.Empty;
    private static string Value(JsonElement owner, string key) => StringValue(Property(owner, key));
    private static string StringValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Object or JsonValueKind.Array => CompactJson(value),
        _ => value.GetRawText()
    };
    private static string CompactJson(JsonElement value)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) value.WriteTo(writer);
        try { return Encoding.UTF8.GetString(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)); }
        finally { CryptographicOperations.ZeroMemory(bytes.GetBuffer()); }
    }

    private static JsonElement Property(JsonElement owner, string key) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(key, out var value) ? value : default;
    private static IEnumerable<JsonElement> Array(JsonElement owner, string key) =>
        Property(owner, key) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray() : [];
    private static JsonElement.ArrayEnumerator RequiredArray(JsonElement owner, string key) =>
        Property(owner, key) is { ValueKind: JsonValueKind.Array } value
            ? value.EnumerateArray() : throw new InvalidDataException("The migration file has an unexpected structure.");
}

public sealed class EncryptedVaultMigrationException() : NotSupportedException("Encrypted Bitwarden export.");
