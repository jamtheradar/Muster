using System.Text.RegularExpressions;

namespace Muster.Core.Notifications;

/// <summary>
/// Reads the unread count out of a document title, e.g. <c>(3) Chat | Microsoft Teams</c>.
/// </summary>
/// <remarks>
/// This is the badge source of record, not a fallback. It is boring, it has no DOM coupling and
/// it will still work in two years, which is exactly why it is the primary signal rather than
/// anything scraped out of the page. See SPEC section 7.1.
/// </remarks>
public static partial class TitleUnreadParser
{
    /// <summary>Counts above this are shown as "99+" rather than a number.</summary>
    public const int DisplayCap = 99;

    /// <summary>
    /// Returns the leading count, or zero when the title carries none. A capped title such as
    /// <c>(99+)</c> yields the number without the plus; there is no more precision to be had.
    /// </summary>
    public static int Parse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        var match = LeadingCount().Match(title);

        return match.Success && int.TryParse(match.Groups["count"].Value, out var count)
            ? count
            : 0;
    }

    /// <summary>Renders a count for a badge. Zero renders as empty.</summary>
    public static string Format(int count) => count switch
    {
        <= 0 => string.Empty,
        > DisplayCap => $"{DisplayCap}+",
        _ => count.ToString(System.Globalization.CultureInfo.CurrentCulture),
    };

    // Only a *leading* count. A number anywhere else in a title is far more likely to be part of
    // the page's own text than an unread count, and a wrong badge is worse than no badge.
    [GeneratedRegex(@"^\s*[\(\[]\s*(?<count>\d{1,5})\s*\+?\s*[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingCount();
}
