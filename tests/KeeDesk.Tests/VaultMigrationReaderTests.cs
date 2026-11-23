using System.IO.Compression;
using System.Text;
using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class VaultMigrationReaderTests
{
    [Fact]
    public void BitwardenKeepsNestedFoldersLoginsAndProtectedFields()
    {
        using var source = Json("""
        {
          "encrypted": false,
          "folders": [{"id":"f1","name":"Development / Shared"}],
          "collections": [{"id":"c1","name":"Team"}],
          "items": [
            {"type":1,"name":"Example","folderId":"f1","collectionIds":["c1"],"favorite":true,"notes":"Line 1\nLine 2",
             "login":{"username":"person","password":"example secret","totp":"JBSWY3DPEHPK3PXP",
              "uris":[{"uri":"https://example.test"},{"uri":"https://other.example.test"}],
              "fido2Credentials":[{"credentialId":"example"}]},
             "fields":[{"name":"Password","value":"secondary","type":1}]},
            {"type":3,"name":"Card","collectionIds":["c1"],"card":{"code":"123"}}
          ]
        }
        """);
        using var imported = VaultMigrationReader.ReadBitwarden(source);
        var nested = Assert.Single(Assert.Single(imported.Groups, group => group.Name == "Development").Groups);
        Assert.Equal("Shared", nested.Name);
        var login = Assert.Single(nested.Entries);
        Assert.Equal("person", login.UserName);
        Assert.Equal("example secret", login.Password);
        Assert.Equal("Line 1\nLine 2", login.Notes);
        Assert.True(login.IsFavorite);
        Assert.Equal("https://example.test", login.Url);
        Assert.Equal("https://other.example.test", login.GetString("URL (2)"));
        Assert.Equal("secondary", login.GetString("Password (2)"));
        Assert.True(login.Strings["Password (2)"].IsProtected);
        Assert.True(login.Strings["Bitwarden passkeys (JSON)"].IsProtected);
        Assert.NotNull(TotpGenerator.TryExtractFromEntry(login));
        var card = Assert.Single(Assert.Single(imported.Groups, group => group.Name == "Team").Entries);
        Assert.Equal("123", card.GetString("code"));
    }

    [Fact]
    public void OnePasswordPreservesStandalonePasswordAttachmentsAndSensitiveFieldsThroughKdbx()
    {
        using var archive = CreateOnePassword("""
        {"accounts":[{"attrs":{"accountName":"Personal"},"vaults":[{"attrs":{"name":"Primary"},"items":[
         {"categoryUuid":"005","details":{"password":"standalone"},
          "overview":{"title":"Generated Password"}},
         {"favIndex":1,"categoryUuid":"001","state":"active","createdAt":1700000000,"overview":{"title":"Login","tags":["Team"]},
          "details":{"loginFields":[{"name":"user","designation":"username","value":"sample user"}],
          "sections":[{"fields":[{"title":"OTP","value":{"totp":"JBSWY3DPEHPK3PXP"}},
                                 {"title":"PIN","value":{"concealed":"0123"}},
                                 {"title":"Document","value":{"file":{"documentId":"doc","fileName":"recovery.txt"}}}]}]}}
        ]}]}]}
        """, ["files/doc__recovery.txt"]);
        using var vault = KdbxDatabase.CreateDefault("Test");
        vault.Kdf = KdbxKdfAlgorithm.AesKdf;
        vault.AesKdfRounds = 10;
        vault.RootGroup.AddGroup(VaultMigrationReader.ReadOnePassword(archive));
        using var key = CompositeKey.FromPassword("migration fixture");
        using var encrypted = new MemoryStream();
        KdbxFormat.Save(vault, encrypted, key);
        encrypted.Position = 0;
        using var reopened = KdbxFormat.Load(encrypted, key);
        var group = reopened.FindGroupByUuid(vault.RootGroup.Groups[^1].Groups[0].Groups[0].Uuid)!;
        Assert.Equal("Primary", group.Name);
        Assert.Equal("standalone", group.Entries[0].Password);
        var login = group.Entries[1];
        Assert.True(login.IsFavorite);
        Assert.Contains("Team", login.Tags);
        Assert.Equal("sample user", login.UserName);
        Assert.Equal("0123", login.GetString("PIN"));
        Assert.Equal("fixture attachment", Encoding.UTF8.GetString(login.Binaries["recovery.txt"].ReadData()));
        Assert.NotNull(TotpGenerator.TryExtractFromEntry(login));
    }

    [Fact]
    public void MalformedEncryptedOrIncompleteExportsFailBeforeTheyCanBeApplied()
    {
        using var encrypted = Json("""{"encrypted":true}""");
        Assert.Throws<EncryptedVaultMigrationException>(() => VaultMigrationReader.ReadBitwarden(encrypted));
        using var bad = Json("""{"items":[]} {"trailing":true}""");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => VaultMigrationReader.ReadBitwarden(bad));
        using var incomplete = CreateOnePassword("""
        {"accounts":[{"vaults":[{"items":[{"overview":{"title":"Test"},
          "details":{"documentAttributes":{"documentId":"missing","fileName":"one.txt"}}}]}]}]}
        """, []);
        Assert.Throws<InvalidDataException>(() => VaultMigrationReader.ReadOnePassword(incomplete));
    }

    private static MemoryStream Json(string contents) => new(Encoding.UTF8.GetBytes(contents));

    private static MemoryStream CreateOnePassword(string metadata, string[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("export.data", metadata);
            foreach (var file in files)
                Write(file, "fixture attachment");

            void Write(string path, string contents)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(Encoding.UTF8.GetBytes(contents));
            }
        }
        stream.Position = 0;
        return stream;
    }
}
