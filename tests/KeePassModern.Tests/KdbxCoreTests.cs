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
}
