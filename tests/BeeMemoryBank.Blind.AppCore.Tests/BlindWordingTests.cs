using System.Text.RegularExpressions;
using BeeMemoryBank.BlindMobile.Services.Blind;
using BeeMemoryBank.Core.Services.BlindPhone;

namespace BeeMemoryBank.Blind.AppCore.Tests;

/// <summary>
/// Wording of the sentences the shared core shows (fix round 2, addendum B): a sentence names the source by its name and never hard-codes a gender (the source may be a person or a computer), and no
/// sentence sends the person to a button that no head has any more. Read from the source of <c>Blind.AppCore</c>, so that a new sentence is held to the same rule.
/// </summary>
public sealed class BlindWordingTests
{
    private static readonly Regex StringLiteral = new("\"(?:\\.|[^\"\\\n])*\"", RegexOptions.Compiled);
    private static readonly Regex Gendered = new(@"\b(he|him|his|she|her|hers)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BeeMemoryBank.slnx"))) dir = dir.Parent;
        dir.Should().NotBeNull("the tests run from inside the repository");
        return dir!.FullName;
    }

    /// <summary>Every string literal of the AppCore sources outside comments, with its file.</summary>
    private static List<(string File, string Text)> Literals()
    {
        var root = Path.Combine(RepoRoot(), "libs", "BeeMemoryBank.Blind.AppCore");
        var found = new List<(string, string)>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (var line in File.ReadAllLines(path))
            {
                var code = line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line;
                foreach (Match match in StringLiteral.Matches(code)) found.Add((Path.GetRelativePath(root, path), match.Value));
            }
        }
        found.Should().NotBeEmpty("the sources of Blind.AppCore were read");
        return found;
    }

    [Fact]
    public void NoSentenceOfTheCore_HardCodesAGender()
    {
        var gendered = Literals().Where(l => Gendered.IsMatch(l.Text)).Select(l => $"{l.File}: {l.Text}").ToList();

        gendered.Should().BeEmpty("the source of an account may be a person or a computer: say its name, \"the owner\" or \"them\"");
    }

    [Fact]
    public void NoSentenceOfTheCore_SendsThePersonToAButtonNoHeadHas()
    {
        var gone = Literals().Where(l => Regex.IsMatch(l.Text, @"Disconnect and (wipe|re-pair|set)|Wipe and set|choose Disconnect", RegexOptions.IgnoreCase)).Select(l => $"{l.File}: {l.Text}").ToList();

        gone.Should().BeEmpty("no head has \"Disconnect and wipe\" any more: the way out is to remove the account from the accounts list and add it again");
    }

    [Fact]
    public void TheWordsOfAnAccountThatWaitsForItsOwner_NameTheSource_AndSayThemWithoutAGender()
    {
        BlindInvitationWords.WaitingFor("Alex", "K7F-2QX").Should().Be("Waiting for Alex to confirm. Read this code out to them: K7F-2QX");
        BlindInvitationWords.WaitingFor("the office computer", "K7F-2QX").Should().Be("Waiting for the office computer to confirm. Read this code out to them: K7F-2QX");
        BlindInvitationWords.WaitingFor("Alex", null).Should().Be("Waiting for Alex to confirm.");
        BlindInvitationWords.OlderHub.Should().Be("The owner's Bee Memory Bank is older than 2.7 and cannot take invitations; ask the owner to update, or pair with a code the old way.");
        BlindInvitationWords.Refused("Alex").Should().Be("Alex refused this device. You can remove this account.");
    }

    [Fact]
    public void TheStartErrorOfAnIdentityWithoutItsKey_SaysWhatIsTrue_AndFitsTheBoundOfASentence()
    {
        // The three sentences a start error can carry for an identity the keys of which are missing: each is said whole (not cut), names the way out a head has, and no wipe.
        var sentences = new[]
        {
            "This account's identity is in its database, but its Keystore seed is missing or does not match. Remove this account from the accounts list and add it again.",
            "This account's backup key is missing, though its Keystore seed is there. Remove this account from the accounts list and add it again to get a new one.",
        };
        foreach (var sentence in sentences)
        {
            sentence.Length.Should().BeLessThanOrEqualTo(BlindFailureText.MaxSentence);
            BlindFailureText.Say(new BeeMemoryBank.Sync.Blind.BlindSentenceException(sentence)).Should().Be(sentence);
        }
    }
}
