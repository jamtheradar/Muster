using Muster.Core.Config;
using Muster.Core.Presence;

namespace Muster.Core.Tests;

/// <summary>
/// The journal is what makes crash recovery precise rather than a guess. Its other job is to be
/// unable to break anything: a presence write must never fail because the note about it could not
/// be filed.
/// </summary>
public sealed class PresenceJournalTests
{
    [Fact]
    public void What_goes_in_comes_back_out()
    {
        using var temp = new TempDirectory();
        var journal = new JsonPresenceJournal(Path.Combine(temp.Path, "presence-applied.json"));

        journal.Write([Account("teams-fabrikam"), Account("teams-clientb")]);

        var read = journal.Read();

        Assert.Equal(["teams-fabrikam", "teams-clientb"], read.Select(a => a.ServiceId));
        Assert.Equal(Account("teams-fabrikam"), read[0]);
    }

    [Fact]
    public void A_missing_file_reads_as_nothing_applied()
    {
        using var temp = new TempDirectory();
        var journal = new JsonPresenceJournal(Path.Combine(temp.Path, "never-written.json"));

        Assert.Empty(journal.Read());
    }

    [Fact]
    public void A_corrupt_file_reads_as_nothing_applied_rather_than_throwing()
    {
        // The worst case of an unreadable journal is a Busy that lives until expirationDuration
        // runs out. Throwing here would instead take the shell's startup with it.
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "presence-applied.json");
        File.WriteAllText(path, "{ not a list");

        Assert.Empty(new JsonPresenceJournal(path).Read());
    }

    [Fact]
    public void Writing_creates_the_folder_it_needs()
    {
        using var temp = new TempDirectory();
        var journal = new JsonPresenceJournal(Path.Combine(temp.Path, "nested", "presence.json"));

        journal.Write([Account("teams-fabrikam")]);

        Assert.Single(journal.Read());
    }

    [Fact]
    public void An_unwritable_path_is_swallowed()
    {
        // A directory where the file should be. Nothing can be written, and nothing may throw.
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "presence-applied.json");
        Directory.CreateDirectory(path);

        var journal = new JsonPresenceJournal(path);

        journal.Write([Account("teams-fabrikam")]);
        Assert.Empty(journal.Read());
    }

    private static PresenceAccount Account(string serviceId) => new(
        serviceId,
        "fabrikam",
        "Fabrikam",
        PresenceStrategyKind.Graph,
        "james@fabrikam.example",
        "11111111-1111-1111-1111-111111111111");
}
