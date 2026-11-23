using System.Text.Json;
using System.Text.Json.Serialization;
using KeeDesk.Core.Kdbx;

namespace KeeDesk.Core.Services;

public sealed record SearchProfile(string Name, EntrySearchParameters Parameters);

public static class SearchProfileStore
{
    private const string ProfileKey = "KeeDesk.SearchProfiles.v1";

    public static SearchProfile[] Load(KdbxDatabase database)
    {
        if (!database.Meta.CustomData.TryGetValue(ProfileKey, out var item))
            return [];

        var profiles = JsonSerializer.Deserialize(item.Value, SearchProfileJsonContext.Default.SearchProfileArray);
        if (profiles is null || profiles.Any(profile => profile is null ||
            string.IsNullOrWhiteSpace(profile.Name) || profile.Parameters is null))
            throw new JsonException("Invalid search profiles.");

        return profiles;
    }

    public static bool TrySave(KdbxDatabase database, SearchProfile[] profiles, Func<bool> saveVault)
    {
        var data = database.Meta.CustomData;
        data.TryGetValue(ProfileKey, out var previous);
        var wasModified = database.IsModified;
        if (profiles.Length == 0)
            data.Remove(ProfileKey);
        else
            data[ProfileKey] = new KdbxCustomDataItem
            {
                Value = JsonSerializer.Serialize(profiles, SearchProfileJsonContext.Default.SearchProfileArray)
            };

        database.IsModified = true;
        bool saved = false;
        try
        {
            saved = saveVault();
            return saved;
        }
        finally
        {
            if (!saved)
            {
                if (previous is null)
                    data.Remove(ProfileKey);
                else
                    data[ProfileKey] = previous;
                database.IsModified = wasModified;
            }
        }
    }
}

[JsonSerializable(typeof(SearchProfile[]))]
internal partial class SearchProfileJsonContext : JsonSerializerContext;
