using Muster.Core.Notifications;

namespace Muster.Core.Tests;

public sealed class TitleUnreadParserTests
{
    [Theory]
    [InlineData("(3) Chat | Microsoft Teams", 3)]
    [InlineData("(1) Microsoft Teams", 1)]
    [InlineData("(12) Activity | Microsoft Teams", 12)]
    [InlineData("(99+) Chat | Microsoft Teams", 99)]
    [InlineData("(3) WhatsApp", 3)]
    [InlineData("[4] Something", 4)]
    [InlineData("  (7) Leading whitespace", 7)]
    public void Reads_a_leading_count(string title, int expected)
        => Assert.Equal(expected, TitleUnreadParser.Parse(title));

    [Theory]
    [InlineData("Microsoft Teams")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("(Draft) Untitled")]
    [InlineData("()")]
    public void Reads_zero_when_there_is_no_count(string? title)
        => Assert.Equal(0, TitleUnreadParser.Parse(title));

    [Theory]
    [InlineData("Inbox (5) - Gmail")]
    [InlineData("Q3 (2024) results")]
    [InlineData("Meeting at 3 | Teams")]
    public void Ignores_numbers_that_are_not_a_leading_count(string title)
    {
        // A number in the middle of a title is far more likely to be the page's own text, and a
        // wrong badge is worse than no badge.
        Assert.Equal(0, TitleUnreadParser.Parse(title));
    }

    [Fact]
    public void Zero_in_the_title_is_zero_unread()
        => Assert.Equal(0, TitleUnreadParser.Parse("(0) Chat | Microsoft Teams"));

    [Fact]
    public void An_absurd_count_does_not_throw()
        => Assert.Equal(0, TitleUnreadParser.Parse("(999999999999) Broken"));

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1, "1")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(5000, "99+")]
    public void Formats_for_a_badge(int count, string expected)
        => Assert.Equal(expected, TitleUnreadParser.Format(count));

    [Fact]
    public void Round_trips_a_realistic_teams_sequence()
    {
        // What actually happens over a few minutes in one tab.
        var titles = new[]
        {
            "Microsoft Teams",
            "(1) Chat | Microsoft Teams",
            "(4) Chat | Microsoft Teams",
            "Chat | Microsoft Teams",
        };

        Assert.Equal([0, 1, 4, 0], titles.Select(TitleUnreadParser.Parse));
    }
}
