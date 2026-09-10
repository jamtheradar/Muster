namespace Muster.Graph;

/// <summary>
/// What happened when an account was signed in. The consent case is called out separately
/// because it is the one failure the user cannot fix by trying again: SPEC section 12 lists
/// tenant policy blocking consent for an unverified publisher as a real, expected outcome, and
/// the answer to it is a conversation with that tenant's admin, not a retry button.
/// </summary>
public sealed record GraphSignInResult(bool Succeeded, bool ConsentRequired, string Message)
{
    public static GraphSignInResult Ok(string account)
        => new(true, false, $"Signed in as {account}.");

    public static GraphSignInResult Failed(string message)
        => new(false, false, message);

    public static GraphSignInResult NeedsAdminConsent(string message)
        => new(false, true, message);
}
