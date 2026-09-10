using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Muster.App.Hosting;

/// <summary>
/// Keeps one Muster per config file. A second launch signals the first and exits, rather than
/// standing up a rival set of sessions on the same WebView2 profiles.
/// </summary>
/// <remarks>
/// The identity is the config path, not the executable. Two instances pointed at different
/// config files are genuinely different apps and are allowed to coexist, which is also what
/// makes <c>--config</c> usable while the real instance is running.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private const string Activate = "activate";

    private readonly ILogger _log;
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private CancellationTokenSource? _listening;
    private bool _disposed;

    private SingleInstance(ILogger log, Mutex mutex, bool isOwner, string pipeName)
    {
        _log = log;
        _mutex = mutex;
        _pipeName = pipeName;
        IsOwner = isOwner;
    }

    /// <summary>True when this process is the one that should actually run.</summary>
    public bool IsOwner { get; }

    /// <summary>
    /// Raised when another launch asks this instance to come to the front. Fires on a thread
    /// pool thread, so marshal to the dispatcher before touching the UI.
    /// </summary>
    public event EventHandler? ActivationRequested;

    /// <summary>Claims ownership for <paramref name="configPath"/>, or reports that someone else has it.</summary>
    public static SingleInstance Claim(string configPath, ILogger log)
    {
        var key = KeyFor(configPath);

        // Local\ scopes this to the current logon session, which is the right granularity: two
        // users on one machine each get their own instance.
        var mutex = new Mutex(initiallyOwned: true, $"Local\\Muster.{key}", out var createdNew);

        return new SingleInstance(log, mutex, createdNew, $"Muster.{key}");
    }

    /// <summary>Starts accepting activation requests from later launches. Owner only.</summary>
    public void StartListening()
    {
        if (!IsOwner || _listening is not null)
        {
            return;
        }

        _listening = new CancellationTokenSource();
        _ = ListenAsync(_listening.Token);
    }

    /// <summary>
    /// Asks the running instance to show itself. Called by the losing process just before it
    /// exits. Failure is not worth blocking on: worst case the user clicks the tray icon.
    /// </summary>
    public bool SignalOwner(TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect((int)timeout.TotalMilliseconds);

            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.WriteLine(Activate);
            writer.Flush();

            _log.LogInformation("Another instance is already running; asked it to come to the front");
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not signal the running instance");
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var message = await reader.ReadLineAsync(ct).ConfigureAwait(false);

                if (message == Activate)
                {
                    _log.LogInformation("A second launch asked us to come to the front");
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // A broken pipe must never take the app down; just wait for the next caller.
                _log.LogWarning(ex, "Single instance listener error");
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    // The config path decides identity, so normalise it before hashing: the same file reached by
    // a different spelling must produce the same key.
    private static string KeyFor(string configPath)
    {
        var normalised = Path.GetFullPath(configPath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(hash)[..16];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _listening?.Cancel();
        _listening?.Dispose();

        if (IsOwner)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
