using System.Text;
using KeePassModern.Core.Crypto;
using KeePassModern.Core.Kdbx;
using KeePassModern.Core.Services;

namespace KeePassModern.Tests;

public class KdbxCoreTests
{
    [Theory]
    [InlineData(KdbxCipherAlgorithm.Aes256, KdbxKdfAlgorithm.Argon2id)]
    [InlineData(KdbxCipherAlgorithm.ChaCha20, KdbxKdfAlgorithm.Argon2d)]
    [InlineData(KdbxCipherAlgorithm.Twofish, KdbxKdfAlgorithm.AesKdf)]
    public void Kdbx4_RoundTrip_AllCiphersAndKdfs(KdbxCipherAlgorithm cipher, KdbxKdfAlgorithm kdf)
    {
        using var db = KdbxDatabase.CreateDefault("测试密码库");
        db.Cipher = cipher;
        db.Kdf = kdf;
        // Fast params for unit tests
        db.KdfIterations = 2;
        db.KdfMemoryBytes = 8UL * 1024 * 1024;
        db.KdfParallelism = 1;
        db.AesKdfRounds = 1000;

        var entry = new KdbxEntry
        {
            Title = "GitHub 企业版",
            UserName = "percy@company.com",
            Password = "S3cur3!Pass#2026_Vault",
            Url = "https://github.com/login",
            Notes = "硬件密钥已绑定",
            IsFavorite = true
        };
        entry.SetString(KdbxConstants.FieldOtp, "otpauth://totp/GitHub:percy?secret=JBSWY3DPEHPK3PXP&issuer=GitHub", true);
        entry.SetString("API_Token", "ghp_secret_token_value", true);
        entry.Binaries["recovery.txt"] = new ProtectedBinary(true, Encoding.UTF8.GetBytes("RECOVERY-CODE-1234"));
        entry.CreateHistorySnapshot();
        entry.Password = "N3w!Str0ng#Pass_2027";
        db.RootGroup.Groups[0].AddEntry(entry);

        using var key = CompositeKey.FromPassword("MasterPass!123");
        using var ms = new MemoryStream();
        KdbxFormat.Save(db, ms, key);

        ms.Position = 0;
        using var loaded = KdbxFormat.Load(ms, key);

        Assert.Equal("测试密码库", loaded.Meta.DatabaseName);
        Assert.Equal(cipher, loaded.Cipher);
        Assert.Equal(kdf, loaded.Kdf);
        var loadedEntry = loaded.FindEntryByUuid(entry.Uuid);
        Assert.NotNull(loadedEntry);
        Assert.Equal("GitHub 企业版", loadedEntry.Title);
        Assert.Equal("N3w!Str0ng#Pass_2027", loadedEntry.Password);
        Assert.Equal("ghp_secret_token_value", loadedEntry.GetString("API_Token"));
        Assert.True(loadedEntry.IsFavorite);
        Assert.Single(loadedEntry.History);
        Assert.Equal("S3cur3!Pass#2026_Vault", loadedEntry.History[0].Password);
        Assert.Equal("RECOVERY-CODE-1234", Encoding.UTF8.GetString(loadedEntry.Binaries["recovery.txt"].ReadData()));
    }

    [Fact]
    public void Kdbx31_RoundTrip_And_XmlKeyFileV2()
    {
        var keyFileXml = CompositeKey.GenerateXmlKeyFileV2();
        using var key = CompositeKey.FromPassword("Pass31");
        key.AddKeyFileBytes(Encoding.UTF8.GetBytes(keyFileXml));

        using var db = KdbxDatabase.CreateDefault("KDBX31Vault");
        db.FormatVersion = KdbxConstants.Version31;
        db.AesKdfRounds = 1000;
        var e = new KdbxEntry { Title = "Legacy", UserName = "admin", Password = "Secret31!" };
        db.RootGroup.AddEntry(e);

        using var ms = new MemoryStream();
        KdbxFormat.Save(db, ms, key);

        ms.Position = 0;
        using var loaded = KdbxFormat.Load(ms, key);
        Assert.Equal(KdbxConstants.Version31, loaded.FormatVersion);
        Assert.Equal("Secret31!", loaded.FindEntryByUuid(e.Uuid)?.Password);
    }

    [Fact]
    public void WrongPassword_ThrowsCryptographicException()
    {
        using var db = KdbxDatabase.CreateDefault();
        db.KdfIterations = 2;
        db.KdfMemoryBytes = 8UL * 1024 * 1024;
        db.KdfParallelism = 1;

        using var key1 = CompositeKey.FromPassword("Correct!");
        using var key2 = CompositeKey.FromPassword("Wrong!");
        using var ms = new MemoryStream();
        KdbxFormat.Save(db, ms, key1);

        ms.Position = 0;
        Assert.ThrowsAny<Exception>(() => KdbxFormat.Load(ms, key2));
    }

    [Fact]
    public void Totp_Rfc6238_Vector_And_PasswordGenerator_And_Audit()
    {
        // RFC 6238 SHA1 test vector: secret "12345678901234567890" -> Base32 "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
        var settings = new TotpSettings("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Digits: 8, Period: 30, Algorithm: "SHA1");
        var res = TotpGenerator.Generate(settings, DateTimeOffset.FromUnixTimeSeconds(59));
        Assert.Equal("94287082", res.Code);

        var pwd = PasswordGenerator.GenerateFromCharset(new PasswordGeneratorOptions { Length = 24 });
        Assert.Equal(24, pwd.Length);
        Assert.True(PasswordGenerator.EvaluateStrength(pwd).EntropyBits > 90);

        var pass = PasswordGenerator.GeneratePassphrase(new PasswordGeneratorOptions { WordCount = 5 });
        Assert.Equal(5, pass.Split('-').Length);

        using var db = KdbxDatabase.CreateDefault();
        db.RootGroup.AddEntry(new KdbxEntry { Title = "Weak1", Password = "123456", Url = "https://a.com" });
        db.RootGroup.AddEntry(new KdbxEntry { Title = "Weak2", Password = "123456", Url = "https://b.com" });
        var report = SecurityAuditService.Analyze(db);
        Assert.Equal(2, report.WeakPasswords.Count);
        Assert.Equal(2, report.ReusedPasswords.Count);
        Assert.True(report.SecurityScore < 80);
    }

    [Fact]
    public void FieldReference_Merge_And_CsvImportExport()
    {
        using var db1 = KdbxDatabase.CreateDefault("Vault1");
        var baseEntry = new KdbxEntry { Title = "Main", UserName = "alice", Password = "MainPassword!" };
        var refEntry = new KdbxEntry { Title = "Alias", UserName = "{USERNAME}", Password = $"{{REF:P@I:{baseEntry.Uuid:N}}}" };
        db1.RootGroup.AddEntry(baseEntry);
        db1.RootGroup.AddEntry(refEntry);

        Assert.Equal("MainPassword!", FieldReferenceResolver.Resolve(refEntry.Password, refEntry, db1));

        using var db2 = KdbxDatabase.CreateDefault("Vault2");
        db2.RootGroup.AddEntry(new KdbxEntry { Title = "FromRemote", UserName = "bob", Password = "RemotePass!99" });
        var summary = DatabaseMergeService.Merge(db1, db2);
        Assert.Equal(1, summary.EntriesAdded);

        var csv = ImportExportService.ExportToCsv(db1);
        using var db3 = KdbxDatabase.CreateDefault("Imported");
        int count = ImportExportService.ImportFromCsv(db3, csv);
        Assert.Equal(3, count);
    }

    [Fact]
    public void Maintenance_Duplicate_And_MoveEntry_Works()
    {
        using var db = KdbxDatabase.CreateDefault("MaintTest");
        var g1 = db.RootGroup.Groups[0];
        var g2 = db.RootGroup.Groups[1];

        var e1 = new KdbxEntry { Title = "Dup", UserName = "u", Password = "p", LastModificationTime = DateTimeOffset.UtcNow.AddMinutes(1) };
        e1.CreateHistorySnapshot();
        var e2 = new KdbxEntry { Title = "Dup", UserName = "u", Password = "p", LastModificationTime = DateTimeOffset.UtcNow };
        g1.AddEntry(e1);
        g1.AddEntry(e2);

        var cloned = db.DuplicateEntry(e1);
        Assert.NotEqual(e1.Uuid, cloned.Uuid);
        Assert.EndsWith("副本", cloned.Title);

        Assert.True(DatabaseMaintenanceService.MoveEntryToGroup(db, cloned, g2));
        Assert.Equal(g2, cloned.Parent);

        int dupRemoved = DatabaseMaintenanceService.DeleteDuplicateEntries(db);
        Assert.Equal(1, dupRemoved);

        int histRemoved = DatabaseMaintenanceService.PurgeHistoryOlderThan(db, 0);
        Assert.True(histRemoved >= 1);

        int emptyRemoved = DatabaseMaintenanceService.DeleteEmptyGroups(db);
        Assert.True(emptyRemoved >= 1);
    }

    [Fact]
    public void AdvancedSearch_And_KeePassXml_RoundTrip_Works()
    {
        using var db = KdbxDatabase.CreateDefault("SearchXmlTest");
        var e1 = new KdbxEntry
        {
            Title = "Prod-AWS-01",
            UserName = "ops@acme.io",
            Password = "SuperSecret#99",
            Expires = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var e2 = new KdbxEntry
        {
            Title = "Staging-DB",
            UserName = "dev@acme.io",
            Password = "DevPassword#88",
            Expires = true,
            ExpiryTime = DateTimeOffset.UtcNow.AddDays(5)
        };
        db.RootGroup.Groups[0].AddEntry(e1);
        db.RootGroup.Groups[0].AddEntry(e2);

        var regexHits = EntrySearchService.Search(db, new EntrySearchParameters { Query = @"^Prod-.*-\d{2}$", UseRegex = true });
        Assert.Single(regexHits);
        Assert.Equal("Prod-AWS-01", regexHits[0].Title);

        Assert.Single(EntrySearchService.FindExpired(db));
        Assert.Single(EntrySearchService.FindExpiringWithinDays(db, 7));

        var xml = ImportExportService.ExportToKeePassXml(db);
        using var db2 = KdbxDatabase.CreateDefault("TargetXml");
        var summary = ImportExportService.ImportFromKeePassXml(db2, xml);
        Assert.Equal(2, summary.EntriesAdded);
    }

    [Fact]
    public void AutoTypeSequenceParser_ResolvesPlaceholdersAndKeys()
    {
        using var db = KdbxDatabase.CreateDefault("AutoTypeTest");
        var g = db.RootGroup.Groups[0];
        g.DefaultAutoTypeSequence = "{USERNAME}{TAB 2}{DELAY 150}{PASSWORD}{ENTER}";
        var entry = new KdbxEntry { Title = "App", UserName = "alice", Password = "Secret#123" };
        g.AddEntry(entry);

        var steps = AutoTypeSequenceParser.Parse(entry, db);
        Assert.Equal(5, steps.Count);
        Assert.Equal(new AutoTypeTextStep("alice"), steps[0]);
        Assert.Equal(new AutoTypeKeyStep(AutoTypeKey.Tab, 2), steps[1]);
        Assert.Equal(new AutoTypeDelayStep(150), steps[2]);
        Assert.Equal(new AutoTypeTextStep("Secret#123"), steps[3]);
        Assert.Equal(new AutoTypeKeyStep(AutoTypeKey.Enter, 1), steps[4]);
    }

    [Fact]
    public void AutoTypeMatcher_MatchesWildcardTitleAndDomain()
    {
        using var db = KdbxDatabase.CreateDefault("MatcherTest");
        var gh = new KdbxEntry { Title = "GitHub Enterprise", UserName = "u1", Password = "p1", Url = "https://github.com/login" };
        var aws = new KdbxEntry { Title = "Cloud Root", UserName = "u2", Password = "p2" };
        aws.AutoType.Associations.Add(new KdbxAutoTypeAssociation { Window = "*AWS Console*", KeystrokeSequence = "{USERNAME}{ENTER}" });
        db.RootGroup.Groups[0].AddEntry(gh);
        db.RootGroup.Groups[0].AddEntry(aws);

        var m1 = AutoTypeMatcher.FindMatches(db, "Sign in to GitHub - Google Chrome");
        Assert.Single(m1);
        Assert.Equal(gh.Uuid, m1[0].Entry.Uuid);

        var m2 = AutoTypeMatcher.FindMatches(db, "Safari - AWS Console Production");
        Assert.Single(m2);
        Assert.Equal(aws.Uuid, m2[0].Entry.Uuid);
        Assert.Equal("{USERNAME}{ENTER}", m2[0].Sequence);
    }
}
