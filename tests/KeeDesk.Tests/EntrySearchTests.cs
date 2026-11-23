using System.Xml.XPath;
using KeeDesk.Core.Crypto;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class EntrySearchTests
{
    [Fact]
    public void SimpleSearchCombinesTermsAcrossSelectedFieldsAndExcludesPhrases()
    {
        using var db = new KdbxDatabase();
        var active = AddEntry(db.RootGroup, "Work Mail Server", "Alice");
        var archived = AddEntry(db.RootGroup, "Work Mail Server", "Alice");
        archived.Tags.Add("old archive");
        AddEntry(db.RootGroup, "Work Mail", "Alice").Notes = "Server";
        AddEntry(db.RootGroup, "Personal Mail Server", "Alice");

        var p = new EntrySearchParameters { Query = "ALICE\twork \"mail server\" -\"old archive\"" };
        Assert.Equal([active], EntrySearchService.Search(db, p));
        p.SearchTags = false;
        Assert.Equal([active, archived], EntrySearchService.Search(db, p));
        p.SearchTags = true;
        p.CaseSensitive = true;
        Assert.Empty(EntrySearchService.Search(db, p));
        p.CaseSensitive = false;
        p.Query = "-\"old archive\"";
        Assert.Equal(3, EntrySearchService.Search(db, p).Count);
    }

    [Fact]
    public void SearchPreservesRegexScopeRecycleBinExpirationAndPasswordOptIn()
    {
        using var db = new KdbxDatabase();
        var scope = new KdbxGroup { Name = "Work" };
        db.RootGroup.AddGroup(scope);
        var match = AddEntry(scope, "-Mail", "Alice");
        AddEntry(scope, "Mail", "ALICE-Extra");
        var expired = AddEntry(scope, "Mail", "Alice");
        expired.Expires = true;
        expired.ExpiryTime = DateTimeOffset.UtcNow.AddDays(-1);
        AddEntry(db.RootGroup, "Mail", "Alice");
        var inBin = AddEntry(db.GetOrCreateRecycleBin(), "Mail", "Alice");
        match.Password = "A-test-placeholder";

        var p = new EntrySearchParameters { Query = "^alice$", UseRegex = true, ScopeGroup = scope, ExcludeExpired = true };
        Assert.Equal([match], EntrySearchService.Search(db, p));
        p.Query = "^-Mail$";
        Assert.Equal([match], EntrySearchService.Search(db, p));
        p.Query = "test-placeholder";
        p.UseRegex = false;
        Assert.Empty(EntrySearchService.Search(db, p));
        p.SearchPasswords = true;
        Assert.Equal([match], EntrySearchService.Search(db, p));
        p.Query = "";
        p.ScopeGroup = inBin.Parent;
        Assert.Empty(EntrySearchService.Search(db, p));
        p.IncludeRecycleBin = true;
        Assert.Equal([inBin], EntrySearchService.Search(db, p));
    }

    [Fact]
    public void XPathFindsEntriesByGroupNotesHistoryFieldsAttachmentsAndExtensions()
    {
        using var db = new KdbxDatabase();
        var group = new KdbxGroup { Name = "Mail", Notes = "Private" };
        db.RootGroup.AddGroup(group);
        var entry = AddEntry(group, "Mail Server", "Alice");
        entry.IconId = 3;
        entry.SetString("Telephone", "123456", true);
        entry.Binaries["recovery.pdf"] = new ProtectedBinary(true, [1]);
        entry.Password = "test-history-value";
        entry.CreateHistorySnapshot();
        entry.Password = "test-current-value";
        AddEntry(db.RootGroup, "Other", "Bob");
        AddEntry(db.GetOrCreateRecycleBin(), "Mail Server", "Alice").IconId = 3;

        var p = new EntrySearchParameters { UseXPath = true, Query = "//Group[contains(Notes, 'Private')]/Entry" };
        Assert.Equal([entry], EntrySearchService.Search(db, p));
        p.Query = "//Entry[IconID = 3]/Binary[contains(Key, '.pdf')]/Key/text()";
        Assert.Equal([entry], EntrySearchService.Search(db, p));
        p.Query = "//Entry/String[Key = 'Telephone' and contains(Value, '12345')]";
        Assert.Equal([entry], EntrySearchService.Search(db, p));
        p.SearchCustomFields = false;
        Assert.Empty(EntrySearchService.Search(db, p));
        p.Query = "//Entry[count(History/Entry) = 1]";
        Assert.Empty(EntrySearchService.Search(db, p));
        p.SearchHistory = true;
        Assert.Equal([entry], EntrySearchService.Search(db, p));
        p.Query = "//History/Entry/String[Key = 'Password' and contains(Value,'history')]";
        Assert.Empty(EntrySearchService.Search(db, p));
        p.SearchPasswords = true;
        Assert.Equal([entry], EntrySearchService.Search(db, p));
        p.Query = "//Group[@missing]/Entry | //Entry/String[Key = 'UserName' and kp:matches(Value, '^alice$', 'i')]/Value";
        Assert.Equal([entry], EntrySearchService.Search(db, p));
        p.Query = "//Entry[String[Key = 'Title' and kp:string-compare(Value, 'mail server', 'en-US', 'i') = 0]]";
        Assert.Equal([entry], EntrySearchService.Search(db, p));
        p.Query = "count(//Entry)";
        Assert.Throws<XPathException>(() => EntrySearchService.Search(db, p));
        p.Query = "//Entry[";
        Assert.Throws<XPathException>(() => EntrySearchService.Search(db, p));
    }

    [Fact]
    public void SearchProfilesRoundTripInTheVaultAndRollBackOnSaveFailure()
    {
        using var db = new KdbxDatabase { Kdf = KdbxKdfAlgorithm.AesKdf, AesKdfRounds = 1000 };
        var parameters = new EntrySearchParameters
        {
            Query = "//Entry[@test]",
            UseXPath = true,
            SearchHistory = true,
            SearchPasswords = true,
            ExcludeExpired = true,
            ScopeGroup = db.RootGroup
        };
        byte[] encrypted = [];
        Assert.Empty(SearchProfileStore.Load(db));
        using var key = CompositeKey.FromPassword("test");
        Assert.True(SearchProfileStore.TrySave(db, [new SearchProfile("Weekly audit", parameters)], () =>
        {
            using var output = new MemoryStream();
            KdbxFormat.Save(db, output, key);
            encrypted = output.ToArray();
            return true;
        }));

        using var loaded = KdbxFormat.Load(new MemoryStream(encrypted), key);
        var profile = Assert.Single(SearchProfileStore.Load(loaded));
        Assert.Equal("Weekly audit", profile.Name);
        Assert.Equal(parameters.Query, profile.Parameters.Query);
        Assert.True(profile.Parameters.UseXPath && profile.Parameters.SearchHistory
            && profile.Parameters.SearchPasswords && profile.Parameters.ExcludeExpired);
        Assert.Null(profile.Parameters.ScopeGroup);

        var before = loaded.IsModified;
        Assert.False(SearchProfileStore.TrySave(loaded, [], () => false));
        Assert.Equal(before, loaded.IsModified);
        Assert.Single(SearchProfileStore.Load(loaded));
        Assert.True(SearchProfileStore.TrySave(loaded, [], () => true));
        Assert.Empty(SearchProfileStore.Load(loaded));
    }

    private static KdbxEntry AddEntry(KdbxGroup group, string title, string userName)
    {
        var entry = new KdbxEntry { Title = title, UserName = userName };
        group.AddEntry(entry);
        return entry;
    }
}
