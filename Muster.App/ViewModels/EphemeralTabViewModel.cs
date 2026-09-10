using Muster.Core.Hosting;

namespace Muster.App.ViewModels;

/// <summary>
/// A transient tab opened from a link or the address bar. Lives in the current workspace's
/// profile and is never written to config, so it does not survive a restart.
/// </summary>
public sealed class EphemeralTabViewModel(SessionDescriptor descriptor) : TabViewModel(descriptor)
{
    public override bool CanClose => true;

    // An ephemeral tab has no configured name, so it takes the page's own once there is one.
    protected override void OnTitleUpdated(string? title)
    {
        base.OnTitleUpdated(title);

        if (!string.IsNullOrWhiteSpace(title))
        {
            DisplayName = Shorten(title);
        }
    }

    private static string Shorten(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length <= 28 ? trimmed : trimmed[..27] + "…";
    }
}
