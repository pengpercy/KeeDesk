using KeeDesk.Core.Kdbx;
using KeeDesk.Core.Services;

namespace KeeDesk.Tests;

public class AutoTypeCompatibilityTests
{
    [Fact]
    public void MatchingUsesAllVaultsAndKeepsDistinctWindowSequences()
    {
        using var work = new KdbxDatabase();
        using var personal = new KdbxDatabase();
        var first = AddEntry(work.RootGroup, "Work account");
        var other = AddEntry(personal.RootGroup, "Personal account");
        first.SetString("Site", "Portal", false);
        first.AutoType.Associations.Add(new() { Window = "//*invalid//", KeystrokeSequence = "invalid" });
        first.AutoType.Associations.Add(new() { Window = "*{S:Site}*", KeystrokeSequence = "{USERNAME}{ENTER}" });
        first.AutoType.Associations.Add(new() { Window = "//*bad//", KeystrokeSequence = "invalid" });
        first.AutoType.Associations.Add(new() { Window = "//^Portal - .+$//", KeystrokeSequence = "{PASSWORD}{ENTER}" });
        first.AutoType.Associations.Add(new() { Window = "Portal*", KeystrokeSequence = "{PASSWORD}{ENTER}" });
        other.AutoType.Associations.Add(new() { Window = "Portal*", KeystrokeSequence = "{S:PIN}{ENTER}" });
        other.SetString("PIN", "123", true);

        var matches = AutoTypeMatcher.FindMatches([work, personal], "Portal - Browser");
        Assert.Equal(3, matches.Count);
        Assert.Equal(new[] { "{USERNAME}{ENTER}", "{PASSWORD}{ENTER}", "{S:PIN}{ENTER}" },
            matches.Select(match => match.Sequence));
        Assert.Same(work, matches[0].Vault);
        Assert.Same(personal, matches[2].Vault);
        Assert.Equal(matches[0], AutoTypeMatcher.MatchSelectedEntry(work, first, "Portal - Browser"));
        Assert.Equal(new AutoTypeTextStep("123"),
            AutoTypeSequenceParser.Parse(matches[2].Entry, matches[2].Vault, matches[2].Sequence)[0]);
    }

    [Fact]
    public void MatchingHonorsInheritedEnablementRecycleBinAndDomainConfirmation()
    {
        using var vault = new KdbxDatabase();
        var disabled = new KdbxGroup { EnableAutoType = false };
        var enabled = new KdbxGroup { EnableAutoType = true };
        vault.RootGroup.AddGroup(disabled);
        disabled.AddGroup(enabled);
        var rejected = AddEntry(disabled, "Example disabled");
        var accepted = AddEntry(enabled, "Different title");
        accepted.Url = "https://example.test/login";
        AddEntry(vault.GetOrCreateRecycleBin(), "Example recycled");
        var expired = AddEntry(vault.RootGroup, "Example expired");
        expired.Expires = true;
        expired.ExpiryTime = DateTimeOffset.UtcNow.AddDays(-1);

        Assert.Null(AutoTypeMatcher.MatchSelectedEntry(vault, rejected, "Example disabled"));
        Assert.Empty(AutoTypeMatcher.FindMatches(vault, "unrelated-exampleinvalid"));
        var match = Assert.Single(AutoTypeMatcher.FindMatches(vault, "Example - Browser"));
        Assert.True(match.RequiresConfirmation);
        Assert.Same(accepted, match.Entry);
        Assert.Equal(new[] { accepted }, AutoTypeMatcher.FindManualChoices([vault]).Select(choice => choice.Entry));
    }

    [Fact]
    public void ParsingKeepsStoredTextLiteralAndAppliesOnlySequenceCommands()
    {
        using var vault = new KdbxDatabase();
        var entry = AddEntry(vault.RootGroup, "Entry");
        entry.Password = "{TAB}^%+~{USERNAME}😀";
        entry.Url = "https://example.test/path#hash";
        entry.SetString("Literal", "a{ENTER}b", true);

        var result = AutoTypeSequenceParser.Parse(entry, vault,
            "{C:skip {TAB}}{PASSWORD}{TAB}{S:Literal}{URL:HOST}{+}{{}{}}^+(a{PGDN})~");
        Assert.Equal(new AutoTypeStep[]
        {
            new AutoTypeTextStep(entry.Password),
            new AutoTypeKeyStep(AutoTypeKey.Tab),
            new AutoTypeTextStep("a{ENTER}bexample.test+{}"),
            new AutoTypeCharacterKeyStep('a', AutoTypeModifiers.Control | AutoTypeModifiers.Shift),
            new AutoTypeKeyStep(AutoTypeKey.PageDown) { Modifiers = AutoTypeModifiers.Control | AutoTypeModifiers.Shift },
            new AutoTypeKeyStep(AutoTypeKey.Enter)
        }, result);
        Assert.Throws<FormatException>(() => AutoTypeSequenceParser.Parse(entry, vault, "{USERNAME}{PASSWORD}{CMD:/unknown/}"));
        Assert.Throws<FormatException>(() => AutoTypeSequenceParser.Parse(entry, vault, "{USERNAME}+("));
    }

    [Fact]
    public void PickCharactersPreservesOrderAndSupportsDropdownNavigation()
    {
        using var vault = new KdbxDatabase();
        var entry = AddEntry(vault.RootGroup, "Account");
        entry.Password = "A👩‍💻B4";
        entry.SetString("Code", "c0J", true);
        var sequence = "{PICKCHARS:Password:Count=2,ID=1}{TAB}{PICKCHARS:Code:C=2,Conv=D,Conv-Offset=1,Conv-Fmt=A0}";
        var steps = AutoTypeSequenceParser.Parse(entry, vault, sequence);
        var text = Assert.IsType<AutoTypePickCharactersStep>(steps[0]);
        Assert.True(text.Hide);
        Assert.Equal(4, text.GetCharacters().Length);
        Assert.Equal(new AutoTypeTextStep("B👩‍💻"), Assert.Single(text.CreateSteps([3, 2])));
        Assert.Throws<FormatException>(() => text.CreateSteps([0, 1]).ToList());
        Assert.Throws<FormatException>(() => text.CreateSteps([1]).ToList());
        var arrows = Assert.IsType<AutoTypePickCharactersStep>(steps[2]);
        Assert.Equal(new AutoTypeStep[] { new AutoTypeKeyStep(AutoTypeKey.Down, 3), new AutoTypeKeyStep(AutoTypeKey.Down, 27) },
            arrows.CreateSteps([1, 2]).ToArray());
    }

    [Fact]
    public void KeepsTheTimeBasedCodeAsAnExecutionStepAfterDelays()
    {
        using var vault = new KdbxDatabase();
        var entry = AddEntry(vault.RootGroup, "Example");
        entry.SetString(KdbxConstants.FieldTimeOtpSecretBase32, "JBSWY3DPEHPK3PXP", true);

        var steps = AutoTypeSequenceParser.Parse(entry, vault, "{DELAY 3000}{TIMEOTP}{F2}");
        Assert.Equal(new AutoTypeDelayStep(3000), steps[0]);
        Assert.IsType<AutoTypeTotpStep>(steps[1]);
        Assert.Equal(new AutoTypeKeyStep(AutoTypeKey.F2), steps[2]);
    }

    private static KdbxEntry AddEntry(KdbxGroup group, string title)
    {
        var entry = new KdbxEntry { Title = title };
        group.AddEntry(entry);
        return entry;
    }
}
