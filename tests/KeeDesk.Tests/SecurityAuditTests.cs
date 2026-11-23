using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class SecurityAuditTests
{
    [Fact]
    public async Task SimilarPasswordScanFindsEditsButIgnoresExactDuplicatesAndExcludedEntries()
    {
        using var db = new KdbxDatabase();
        var first = AddEntry(db.RootGroup, "TestPasswordAlpha1");
        var second = AddEntry(db.RootGroup, "TestPasswordAlpha2");
        var duplicate = AddEntry(db.RootGroup, "TestPasswordAlpha1");
        var inserted = AddEntry(db.RootGroup, "TestPasswordAlpha01");
        var unrelated = AddEntry(db.RootGroup, "UnrelatedExample!");
        var optedOut = AddEntry(db.RootGroup, "TestPasswordAlpha3");
        optedOut.QualityCheck = false;
        var recycled = AddEntry(db.GetOrCreateRecycleBin(), "TestPasswordAlpha4");

        var matches = await SecurityAuditService.FindSimilarPasswordsAsync(db);
        Assert.All(matches, pair => Assert.NotEqual(pair.First.Password, pair.Second.Password));
        var affected = matches.SelectMany(pair => new[] { pair.First, pair.Second }).ToHashSet();
        Assert.True(affected.SetEquals([first, second, inserted, duplicate]));
        Assert.DoesNotContain(unrelated, affected);
        Assert.DoesNotContain(optedOut, affected);
        Assert.DoesNotContain(recycled, affected);
        Assert.True(matches.First().Similarity > 0.9);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SecurityAuditService.FindSimilarPasswordsAsync(db, cancellation.Token));
    }

    private static KdbxEntry AddEntry(KdbxGroup group, string password)
    {
        var entry = new KdbxEntry { Password = password };
        group.AddEntry(entry);
        return entry;
    }
}
