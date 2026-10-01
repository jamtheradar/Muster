using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Muster.App.Configuration;
using Muster.App.Hosting;
using Muster.App.Logging;
using Muster.App.Notifications;
using Muster.App.Tray;
using Muster.App.ViewModels;
using Muster.App.Views;
using Muster.Core.Config;
using Muster.Core.Diagnostics;
using Muster.Core.Hosting;
using Muster.Core.Notifications;
using Muster.Core.Presence;
using Muster.Graph;
using Velopack;

namespace Muster.App;

/// <summary>
/// Composition root. Everything is constructor injected from here; there is no service locator.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before everything, including base.OnStartup. Velopack's install, update and uninstall
        // hooks arrive as switches on this same exe and exit the process when they are done; a
        // hook run must not reach the logger, claim the single-instance mutex, or touch the
        // config on its way past.
        VelopackApp.Build().Run();

        base.OnStartup(e);

        var configPath = ReadConfigPathArgument(e.Args) ?? JsonConfigStore.DefaultPath;
        var configStore = new JsonConfigStore(configPath);

        // Logging has to be running before anything that might need diagnosing, which includes
        // loading the config itself. Read just the logging section here, best effort: an unusable
        // config is reported properly by the shell, and defaults are enough to log that with.
        var logging = ReadLoggingConfig(configStore);

        // --verbose turns on the Debug-level diagnostics: unread breakdowns, routing decisions.
        // It only ever turns the detail up, so a config asking for Trace still gets Trace.
        var verbose = e.Args.Any(a => a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
        if (verbose && logging.Level > LogVerbosity.Debug)
        {
            logging = logging with { Level = LogVerbosity.Debug };
        }

        // The floor is what makes --verbose survive. LiveSettings.Apply pushes the config's level
        // in at startup and on every reload, and without this each of those puts the detail back
        // down again.
        var fileLogs = new FileLoggerProvider(logging)
        {
            Floor = verbose ? LogVerbosity.Debug : LogVerbosity.None,
        };
        var level = FileLoggerProvider.ToLogLevel(logging.Level);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            // Floor of Debug so the provider's own live level change has room to move without a
            // restart; the file sink still filters to whatever the settings screen last set.
            builder.SetMinimumLevel(level < LogLevel.Debug ? level : LogLevel.Debug);
            builder.AddDebug();
            builder.AddProvider(fileLogs);
        });

        // One Muster per config file. A second launch hands over to the first and exits, rather
        // than standing up rival sessions on the same WebView2 profiles.
        var startupLog = LoggerFactory.Create(b => b.AddProvider(fileLogs)).CreateLogger<App>();
        _singleInstance = SingleInstance.Claim(configPath, startupLog);

        if (!_singleInstance.IsOwner)
        {
            _singleInstance.SignalOwner(TimeSpan.FromSeconds(3));
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        // Old files go before anything appends to today's, so the folder the settings screen
        // shows is the folder that will still be there tomorrow.
        var swept = LogFolder.Prune(fileLogs.Directory, logging.RetentionDays, DateTimeOffset.Now);
        startupLog.LogInformation(
            "Logging at {Level} to {Directory}, {Swept} expired file(s) removed",
            logging.Level,
            fileLogs.Directory,
            swept);

        services.AddSingleton(fileLogs);
        services.AddSingleton(DiagnosticOptions.FromArgs(e.Args));
        services.AddSingleton<IConfigStore>(configStore);
        services.AddSingleton<IWebViewEnvironment, WebViewEnvironment>();
        services.AddSingleton<SessionManager>();

        // Before the tray icon and the toast service, both of which read the current set at
        // construction and then follow it.
        services.AddSingleton<AppIcons>();
        services.AddSingleton<TrayIcon>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<NotificationCoordinator>();

        // These four start on their defaults and are corrected by LiveSettings.Apply the moment
        // the shell has actually loaded a config, which is also how every later reload reaches
        // them. Reading the file here as well would be a second loader to keep in step, and the
        // window between composition and load is one nothing can happen in.
        services.AddSingleton(_ => new CurrentConfig());
        services.AddSingleton(_ => new NotificationHub(NotificationConfig.DefaultHistoryLimit));
        services.AddSingleton(_ => new PresenceCoordinator(new PresenceConfig()));
        services.AddSingleton(_ => new SuspensionCoordinator(new SuspensionConfig()));

        // Presence writing. The applier is the only thing in the app that changes anyone's
        // status; the strategies are how it does so, one per presenceStrategy config value.
        // 'dom' has none, deliberately: the validator rejects it rather than letting a config ask
        // for a strategy that would quietly do nothing.
        services.AddSingleton<GraphPresenceOptions>();
        services.AddSingleton<IGraphTokenProvider, GraphTokenProvider>();
        services.AddSingleton<IPresenceJournal>(_ => new JsonPresenceJournal());
        services.AddSingleton<IPresenceStrategy>(provider => new GraphPresenceStrategy(
            provider.GetRequiredService<IGraphTokenProvider>(),
            new HttpClient
            {
                BaseAddress = GraphPresenceStrategy.DefaultBaseAddress,

                // Presence is best effort and sits on the path of a call starting. A Graph call
                // that has not answered in ten seconds is one to give up on and log.
                Timeout = TimeSpan.FromSeconds(10),
            },
            provider.GetRequiredService<ILogger<GraphPresenceStrategy>>()));
        // The page strategy: Teams' own presence service, called as the signed-in tab. The
        // fallback for a tenant that will not consent to the app registration, which here is most
        // of them. The tracker holds live tokens in memory and is registered once, under both its
        // own type and the interface, so the strategy and the sessions share one.
        services.AddSingleton<TeamsPresenceTracker>();
        services.AddSingleton<ITeamsPresenceSessionSource>(
            provider => provider.GetRequiredService<TeamsPresenceTracker>());
        services.AddSingleton<IPresenceStrategyLog, PresenceStrategyLog>();
        services.AddSingleton<IPresenceStrategy>(provider => new PagePresenceStrategy(
            provider.GetRequiredService<ITeamsPresenceSessionSource>(),
            new HttpClient
            {
                // Same reasoning as the Graph client: presence sits on the path of a call starting
                // and must never be what makes the shell feel stuck.
                Timeout = TimeSpan.FromSeconds(10),
            },
            provider.GetRequiredService<IPresenceStrategyLog>()));

        services.AddSingleton<PresenceApplier>();

        services.AddSingleton(provider => new ConfigWatcher(provider.GetRequiredService<IConfigStore>()));
        services.AddSingleton<LiveSettings>();
        services.AddSingleton<ExternalLinkOpener>();
        services.AddSingleton<PinnedShortcut>();
        services.AddSingleton<RelaunchProperties>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        // A fresh settings screen each time it is opened, so cancelling really does discard.
        // The window takes the factory rather than the provider: still constructor injection,
        // and it cannot be used to reach for anything else.
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        services.AddSingleton<Func<SettingsWindow>>(
            provider => provider.GetRequiredService<SettingsWindow>);

        // Same shape, and transient for a plainer reason: the window owns a WebView2 holding a
        // microphone, so it is built when asked for and disposed when closed rather than kept.
        services.AddTransient<DeviceCheckWindow>();
        services.AddSingleton<Func<DeviceCheckWindow>>(
            provider => provider.GetRequiredService<DeviceCheckWindow>);

        _services = services.BuildServiceProvider();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // The dispatcher handler only ever saw half of it. An exception on a thread-pool thread —
        // a timer callback, an async void continuation that had left the UI thread, a WebView2
        // completion — takes the process down with no managed handler anywhere, which presents as
        // the app vanishing and the log simply stopping mid-sentence. These two are the difference
        // between a crash you can read about and one you can only guess at.
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // The tray icon outlives the window: closing to tray must not take the app down.
        _services.GetRequiredService<TrayIcon>();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        // Fires on a thread pool thread; the window must only be touched on the dispatcher.
        _singleInstance.ActivationRequested += (_, _) =>
            Dispatcher.BeginInvoke(window.BringToFront);

        _singleInstance.StartListening();
    }

    /// <summary>
    /// <c>--config &lt;path&gt;</c> points the app at a different muster.json. Exists so a throwaway
    /// multi-workspace config can be exercised without disturbing the real one.
    /// </summary>
    private static string? ReadConfigPathArgument(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the logging section before anything else exists to log with. Any failure falls back
    /// to the defaults: the shell reports a bad config properly a moment later, and it needs a
    /// working log to report it into.
    /// </summary>
    private static LoggingConfig ReadLoggingConfig(IConfigStore store)
    {
        try
        {
            return store.LoadAsync().GetAwaiter().GetResult().Logging;
        }
        catch (Exception)
        {
            return new LoggingConfig();
        }
    }

    /// <summary>
    /// Last word before the process dies. Written synchronously, because there is no "later".
    /// </summary>
    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var log = _services?.GetService<ILoggerFactory>()?.CreateLogger<App>();

        if (e.ExceptionObject is Exception exception)
        {
            log?.LogCritical(exception, "Unhandled exception, terminating {Terminating}", e.IsTerminating);
        }
        else
        {
            log?.LogCritical("Unhandled non-exception thrown, terminating {Terminating}", e.IsTerminating);
        }
    }

    /// <summary>
    /// A faulted task nobody awaited. Not fatal by default on modern .NET, but it is how a
    /// fire-and-forget failure hides, and several things here are deliberately fire-and-forget.
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _services?.GetService<ILoggerFactory>()
            ?.CreateLogger<App>()
            .LogError(e.Exception, "Unobserved exception in a fire-and-forget task");

        // Observed now, so it cannot escalate.
        e.SetObserved();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetService<ILoggerFactory>()
            ?.CreateLogger<App>()
            .LogCritical(e.Exception, "Unhandled exception on the dispatcher");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
