namespace Muster.Core.Presence;

/// <summary>
/// What is needed to speak to Teams' own presence service as one signed-in session, lifted from
/// that session's live traffic. See SPEC section 8.3, strategy <c>page</c>.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is observed rather than constructed. The endpoint carries a region and a
/// version — <c>/ups/apac/v1/</c> for a tenant homed in APAC — and building that string ourselves
/// would break for a client homed elsewhere, silently, which is the worst way for this particular
/// feature to fail. Capturing it means region and version drift cost nothing.
/// </para>
/// <para>
/// <see cref="Authorization"/> is a live bearer token. It must never be logged, never written to
/// disk, and never leave the process. It is held only in memory, and only for as long as the
/// session that produced it is running.
/// </para>
/// </remarks>
/// <param name="Endpoint">Base of the presence API, ending in a slash, e.g. <c>https://teams.cloud.microsoft/ups/apac/v1/</c>.</param>
/// <param name="Authorization">The <c>authorization</c> header value, verbatim. Secret.</param>
/// <param name="Cookie">The <c>Cookie</c> header value, verbatim. Secret.</param>
/// <param name="Mri">
/// This session's own identity, e.g. <c>8:orgid:{guid}</c>. Needed only to read presence back;
/// null when it has not been observed yet, which costs verification but not the write.
/// </param>
public sealed record TeamsPresenceSession(
    Uri Endpoint,
    string Authorization,
    string Cookie,
    string? Mri)
{
    /// <summary>Whether this is usable for a write at all.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Authorization);

    /// <summary>Whether presence can be read back to confirm a write actually landed.</summary>
    public bool CanVerify => IsUsable && !string.IsNullOrWhiteSpace(Mri);

    /// <summary>
    /// Deliberately overridden. The compiler-generated record ToString prints every property,
    /// which for this type means printing a bearer token into whatever was formatting it.
    /// </summary>
    public override string ToString()
        => $"TeamsPresenceSession {{ Endpoint = {Endpoint}, Mri = {Mri ?? "unknown"}, secrets withheld }}";
}
