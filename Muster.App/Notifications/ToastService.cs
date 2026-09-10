using System.IO;
using CommunityToolkit.WinUI.Notifications;
using Microsoft.Win32;
using Microsoft.Extensions.Logging;
using Muster.App.Configuration;
using Muster.Core.Notifications;

namespace Muster.App.Notifications;

/// <summary>
/// Native Windows toasts. Clicking one carries the notification id back, which is what lets the
/// shell activate the right workspace and tab, and report the click to the originating page.
/// </summary>
public sealed class ToastService : IDisposable
{
    private const string ArgumentName = "muster-notification";

    private readonly AppIcons _icons;
    private readonly ILogger<ToastService> _log;

    private bool _subscribed;
    private bool _named;
    private bool _disposed;

    public ToastService(AppIcons icons, ILogger<ToastService> log)
    {
        _icons = icons;
        _log = log;

        // The sender name and icon live in the registry, so a changed set is only picked up by
        // rewriting them. Deferred to the next toast rather than done here: the key belongs to the
        // toolkit and does not exist until it has registered one.
        _icons.Changed += OnIconSetChanged;
    }

    /// <summary>
    /// Raised with the notification id when a toast is clicked. Fires on a background thread.
    /// </summary>
    public event EventHandler<string>? Activated;

    /// <summary>Shows a toast. Failures are logged and swallowed: a toast is never load-bearing.</summary>
    public void Show(NotificationRecord record)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            EnsureRegistered();

            new ToastContentBuilder()
                .AddArgument(ArgumentName, record.Id)
                .AddText(record.ServiceName)
                .AddText(record.Title)
                .AddText(record.Body)
                .Show();

            // After the first Show, not before: the toolkit rewrites the AUMID key as part of
            // registering, which wipes anything set earlier.
            NameTheApp();
        }
        catch (Exception ex)
        {
            // Unpackaged toast registration can fail for reasons entirely outside our control,
            // for instance when Focus Assist or policy blocks notifications. The badge and the
            // panel still work, so this is never fatal.
            _log.LogWarning(ex, "Could not show a toast for {Notification}", record.Id);
        }
    }

    private void EnsureRegistered()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    /// <summary>
    /// For an unpackaged app the toolkit registers an AUMID keyed on the executable path but
    /// leaves DisplayName empty, so Windows labels every toast with the full path. Fill in the
    /// name and icon on the key it already created; never create one ourselves.
    /// </summary>
    private void NameTheApp()
    {
        if (_named)
        {
            return;
        }

        _named = true;

        try
        {
            var exe = Environment.ProcessPath;

            if (string.IsNullOrEmpty(exe))
            {
                return;
            }

            // The toolkit writes the path with forward slashes; try both spellings.
            foreach (var aumid in new[] { exe.Replace('\\', '/'), exe })
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\AppUserModelId\{aumid}", writable: true);

                if (key is null)
                {
                    continue;
                }

                key.SetValue("DisplayName", "Muster", RegistryValueKind.String);

                var icon = _icons.CurrentPath;
                if (File.Exists(icon))
                {
                    key.SetValue("IconUri", icon, RegistryValueKind.String);
                }

                _log.LogDebug("Named the toast sender on {Aumid}", aumid);
                return;
            }
        }
        catch (Exception ex)
        {
            // Purely cosmetic; a toast with an ugly sender name still works.
            _log.LogDebug(ex, "Could not set the toast display name");
        }
    }

    private void OnIconSetChanged(object? sender, EventArgs e) => _named = false;

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        try
        {
            var arguments = ToastArguments.Parse(e.Argument);

            if (arguments.TryGetValue(ArgumentName, out var id) && !string.IsNullOrEmpty(id))
            {
                Activated?.Invoke(this, id);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read a toast activation argument");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icons.Changed -= OnIconSetChanged;

        if (_subscribed)
        {
            ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
        }

        // Leaves no stale toasts pointing at a process that has gone away.
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not clear toast history on shutdown");
        }
    }
}
