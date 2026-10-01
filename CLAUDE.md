# CLAUDE.md

Guidance for Claude Code working in this repository. Read `SPEC.md` before making structural
changes, and `STATUS.md` for where the build actually is, what is deferred, and what still needs
verifying by hand.

## What this is

A Windows-only WPF shell hosting multiple isolated web sessions in tabs, grouped into workspaces, with cross-account Microsoft Teams presence sync. It replaces running several Edge profiles side by side.

It is deliberately **not** a general-purpose service aggregator. See "Scope discipline" below.

## Stack

- .NET 10. `Muster.App` is `net10.0-windows10.0.19041.0` with `UseWPF`; `Muster.Core` and the
  tests are plain `net10.0`
- `Microsoft.Web.WebView2` for hosting
- `CommunityToolkit.Mvvm` for MVVM (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`)
- `Microsoft.Extensions.DependencyInjection`, `.Logging`, `.Configuration`
- `CommunityToolkit.WinUI.Notifications` for toasts
- `Microsoft.Identity.Client` for MSAL
- Graph called over `HttpClient` unless the SDK earns its weight

No other DI container, no MVVM framework beyond the toolkit, no third-party UI library without asking first.

## Project layout

```
Muster.App        WPF host. Views, ViewModels, injected JS, tray, toasts.
Muster.Core       Domain. Config, session descriptors, notifications, presence, routing.
Muster.Core.Tests xunit. Golden-file config round trip, presence state machine, Graph client.
Muster.Graph      Graph presence client and MSAL token cache. Plain net10.0, no WPF.
```

`Muster.Core` targets `net10.0`, not `net10.0-windows`, so a stray WPF reference is a compile
error rather than a code review comment. Keep it that way.

Run the app against a throwaway config with `Muster.exe --config <path>` rather than editing the
real `%APPDATA%\Muster\muster.json`.

**`Muster.Core` must not reference WPF or WebView2.** Everything it needs from a hosted page arrives through `IHostedSession` as plain events. This is what makes the presence state machine and notification pipeline testable without launching a browser. If you find yourself wanting a `CoreWebView2` type in Core, add an abstraction instead.

## Conventions

- File-scoped namespaces, nullable enabled, `TreatWarningsAsErrors` on
- `async`/`await` throughout, `CancellationToken` on anything that can be cancelled, no `async void` outside event handlers
- Constructor injection only, no service locator
- ViewModels hold no WebView2 references. The view layer owns controls; ViewModels own state.
- One class per file, named for the class
- Prefer records for config and message DTOs

## Things that will bite you

**Never reassign `WebView2.Source` to switch tabs.** It destroys the session and you lose auth and notifications. Every live session is its own control; switch with `Visibility`.

**Hide inactive sessions with `Visibility.Hidden`, never `Collapsed`.** WebView2 is an `HwndHost`: a collapsed element is never arranged, so its child window is never created and the session never loads. Hidden keeps it alive and loading in the background, which is what `keepAlive` services need.

**Nothing WPF can be drawn on top of a live session.** WebView2 is an `HwndHost`, so its child
window paints over every WPF element sharing that space no matter what the z-order says, and no
amount of `Panel.ZIndex` changes it. Overlay panels therefore appear only when no session happens
to cover them, which reads as an intermittent bug rather than a layout rule. Give the panel its
own grid column or row and let the session be laid out smaller — `NotificationPanel` in
`MainWindow.xaml` is the pattern. If an overlay genuinely has to float, it needs its own HWND
(a `Popup`), with the window-follow and dismiss handling that implies.

**Do not set `InvariantGlobalization`.** It makes WPF data binding throw `Cannot find non-neutral culture related to 'en-us'` the moment the first binding activates, and the stack trace points at `Window.Show()` rather than at the cause.

**Teams runs on `teams.cloud.microsoft`, not `teams.microsoft.com`.** The old host just redirects. It is the new origin that requests microphone, camera and notifications, so it is the one that has to be on the permission allow list.

**Teams asks for more than microphone, camera and notifications.** The one that surprises people
is `WindowManagement` — "teams.cloud.microsoft wants to manage windows on all your displays" —
raised the first time a meeting or a popped-out chat wants a second monitor. `ClipboardRead`,
`LocalFonts`, `Autoplay` and `MultipleAutomaticDownloads` turn up in normal use too.
`HostedSession.AutoGranted` is the list; everything not on it still prompts, deliberately.
Grant with `SavesInProfile = true`, which also overrides a Block the user clicked before the list
grew.

**Suspension lives on `CoreWebView2`, not `CoreWebView2Controller`,** whatever SPEC 6.1 used to
say: in the .NET SDK it is `CoreWebView2.TrySuspendAsync()`, `Resume()` and `IsSuspended`. The
controller must be invisible when you call it or it throws `ERROR_INVALID_STATE`; the WPF control
forwards `UIElement.IsVisible` through, so `View.IsVisible` is the thing to check. Suspending is
best effort — a page holding audio or a download simply returns false — so treat a refusal as a
memory cost, never an error.

**Never cache "is this session suspended".** WebView2 resumes on its own when the control becomes
visible and for some navigations, so a remembered flag goes stale with nothing to tell you.
`HostedSession.IsSuspended` reads through to `CoreWebView2` every time, and
`SuspensionCoordinator` deliberately keeps no such flag: it re-announces every eligible session on
each tick and lets the host, which can see the real state, no-op. A design that remembered would
get stuck holding a session awake after a single background navigation.

**Register injected scripts with `AddScriptToExecuteOnDocumentCreatedAsync` before first navigation.** Injecting after load misses the wrappers.

**`NotificationReceived` covers non-persistent notifications only.** Anything raised from a service worker will not appear. That is why `bridge.js` shims `ServiceWorkerRegistration.prototype.showNotification`, and why title parsing is the badge source of record rather than a fallback.

**A control offered as `e.NewWindow` must not have navigated yet,** and it needs a realised window handle first. Order is: create the control, add it to the visual tree, `EnsureCoreWebView2Async`, then assign `e.NewWindow`, then complete the deferral. Navigating first makes WebView2 reject it.

**Route scripted popups to their own session, never into an existing tab.** `window.open` with window features is how Entra sign-in arrives; reusing a pinned tab breaks `window.opener` and throws away that tab's running app. `WindowFeatures.HasSize || HasPosition` is the tell.

**A scripted popup gets a real top-level window, not a tab.** A page asking for width and height
is asking for a window: a popped-out Teams meeting is useless trapped in the tab strip, and a
sign-in prompt in the tab strip reads as a hijacked tab. `PopupWindow` hosts it, still on the
workspace's own profile — the shell's *window* boundary is what gets escaped, never its session
isolation, so this is not the system-browser fall-through the app exists to prevent. Order
matters and is unforgiving: show the window first so it has a realised handle, then create the
session into `window.Host`, then assign `e.NewWindow`, then complete the deferral.

**Floating windows are unowned, so `MainWindow.OnClosed` has to close them itself.** An owner
would pin them above the shell in the z-order, which defeats popping a meeting out to sit beside
it. The price is that WPF will not tear them down for you: without the explicit loop, quitting
leaves orphaned meeting windows with no way back to the app. `ShutdownMode` is
`OnExplicitShutdown`, so closing the last one never exits the app by accident.

**Pop-out reopens a URL; it does not move the running session.** A WebView2 control cannot be
reparented between windows without its controller being torn down and rebuilt, which costs the
sign-in and drops whatever the page was mid-way through — the thing the rest of the shell exists
to avoid. So `PopOutActiveTabAsync` starts a fresh session at the tab's *current* URL
(`HostedSession.CurrentUri`, not `Descriptor.Home`). The consequence to state plainly whenever
this comes up: a call already running in the tab does not travel with it.

**Popping out a pinned service leaves its tab running, so the window must not deliver its
notifications twice.** An ephemeral tab genuinely moves and closes behind you; a pinned one cannot,
because its session *is* the service. The second window is therefore marked
`PopupWindow.IsDuplicateView`, and `ResolveDetachedSession` returns null for it — the still-running
tab stays the source of record. Get this wrong and every Teams message arrives twice, with two
different session ids, so the dedupe key will not save you.

**A floating session has no tab, and everything keyed on tabs has to say so.** Titles go to the
window, `window.close()` closes the window, a notification click activates the window, and
`NotificationCoordinator.ResolveDetachedSession` is what stops its notifications being dropped as
"unknown session". `SessionDescriptor.IsFloating` keeps it out of tab-visibility switching, and it
is `KeepAlive` so the suspension policy cannot put a meeting to sleep while you are watching it.

**Set `Handled = true` before calling `ReportShown`, `ReportClicked` or `ReportClosed`,** or they fail with `ERROR_INVALID_STATE`. Handled cannot be un-set once true.

**`MediaStreamTrack.stop()` does not fire the `ended` event.** `ended` only fires when the device disappears, so `bridge.js` wraps `stop()` as well. Watching only `ended` means a call that the user hangs up normally never appears to end.

**Count microphone acquisitions per session; never treat them as a toggle.** Teams runs across several frames, each with its own copy of `bridge.js`, and a page can hold more than one stream at once. A session is in a call while its count is above zero. `bridge.js` also releases on `pagehide`, and the host calls `ReportSessionGone` when a tab closes, or a session that goes away mid-call leaves the state machine stuck `InCall`.

**A microphone test must never run inside a `HostedSession`.** Every hosted session gets
`bridge.js`, and `bridge.js` reports acquisition to `PresenceCoordinator` — so a device test opening
the microphone through one is indistinguishable from joining a call, and would set the user Busy in
every configured tenant while they checked their headset. `DeviceCheckWindow` therefore owns a
plain `WebView2` with its own permission handler and no injected script, on a profile of its own.
Nothing under `Muster.Core/Presence` had to change to add the feature, and that is the test of
whether it is still wired correctly.

**Test audio in a WebView2, not against WASAPI.** Teams' audio is Chromium's: its device
enumeration, its default-device pick, its capture graph. A native test exercises a different stack
and can pass while Teams stays silent — which under RDP, where the redirected device is the thing
that half-works, is the likely case rather than the far-fetched one. Confirmed on 2026-09-15: the
check reported `Default - Remote Audio`, 48 kHz, one channel, which is exactly the device Teams had.

**The mic meter has no silence alarm, deliberately.** An open, quiet microphone is what everyone
listening in a meeting has, so a timer-based "no signal" warning fires through every call you sit
quietly in — and it cannot tell that from the case it exists for, talking into a dead microphone,
by elapsed time alone. The meter is the indicator: speak, and it moves. `MicSignalMonitor` adds
only the two things a bare meter cannot say, and both are load-bearing:

- **The track's `enabled` flag, reported with every level.** Teams' mute button leaves the track
  open and feeds digital silence, so without it muted and broken look identical — the single most
  misleading thing this could show.
- **When sound was last heard.** "Quiet, nothing ever picked up" is the shape of a dead microphone;
  "quiet, last heard nine seconds ago" is the shape of someone listening.

**Draw a level meter on a decibel scale.** Speech RMS sits in the bottom tenth of a linear meter,
so a linear bar barely moves while someone talks — the exact wrong answer from a control whose only
job is to say whether the microphone works. `LevelToWidthConverter` maps a 60 dB window, and the
page does the same.

**`bridge.js`'s analyser is never connected to the context's destination,** and only one track per
frame is metered. Connecting it plays the microphone back through the speakers mid-call; metering
every track builds an audio graph per stream inside someone's meeting. The `AudioContext` is also
closed when metering stops rather than left idle, because it holds an output device open and one
outliving its call is how an unrelated audio problem starts looking like Muster's fault.

**`CoreWebView2.IsDocumentPlayingAudio` is the speaker indicator, and it costs nothing.** WebView2
already knows which documents are rendering audio, so the tab mark needs no shim and no parsing. It
answers the other half of "is my headset working": if Teams is playing and you hear nothing, the
fault is between the browser and your ears rather than in the call.

**Every coordinator event fires on a timer thread, and marshalling them is not tidiness.**
`SuspensionCoordinator` and `PresenceCoordinator` both raise from `TimeProvider` callbacks, and
almost everything a handler wants to touch is a WebView2 control or a WPF collection. Getting this
wrong killed the app silently for days: `OnSuspendRequested` was `async void`, so it ran on the
timer thread as far as `HostedSession.Core` before its first await, WPF's `VerifyAccess` threw, and
an `async void` with no synchronization context hands that straight to the thread pool where
nothing catches it. The process died with no log line, roughly `idleMinutes` after launch, looking
for all the world like a random crash.

Two rules follow. **Marshal at the handler with `Dispatcher.BeginInvoke`, and put the `async` work
in a separate method the lambda kicks off** — making the handler itself `async void` and awaiting
inside it still runs the first synchronous stretch on the timer thread, which is exactly where the
fault was. And **`async void` outside a genuine UI event handler is how a crash becomes invisible**;
if it must exist, it needs its own try/catch around everything.

**`App` handles `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, not
just `DispatcherUnhandledException`.** The dispatcher handler only ever saw the UI thread, so any
fault on a timer or thread-pool thread terminated the process with an empty log — which is
indistinguishable from a hard native crash and sends you looking in the wrong place entirely. Do
not remove them; several things in the shell are deliberately fire-and-forget.

**Anything set from the command line has to be defended against `LiveSettings.Apply`, not just
set once.** Apply runs at startup *and* on every config reload, and it pushes the file's values
into the running app wholesale. `--verbose` used to lose to it about half a second after launch —
the startup line said Debug while the file was filtered to Information, which is worse than not
having the flag. `FileLoggerProvider.Floor` is the fix, and the shape to copy: the component owns
the limit, rather than every caller of Apply having to remember the exception.

**`PresenceApplier` is the only thing in the app that writes presence.** Everything else reports
into it: `PresenceCoordinator` says whether you are on a call, the config says which accounts
exist, the tray item and the hotkey say whether Busy is forced. It works by computing the set that
*should* be Busy and reconciling against the set that already is, which is what makes the awkward
cases fall out rather than need handling — a session that joins a call mid-way stops being a target
and is cleared, and an account deleted from the config while Busy is still cleared, because it is
held in the applied set rather than looked up in the config. Add a new reason to be Busy by
changing `Desired()`, never by calling a strategy from somewhere else.

**Presence is re-sent long before it expires.** `expirationDuration` is a crash safety net, not a
statement about how long calls run: a meeting past the two hours would otherwise drop back to
Available mid-sentence with nothing anywhere to say why. `PresenceApplier` renews at half the
expiry, floored at 5 minutes and capped at an hour, and the same tick retries whatever failed last
time. Shortening the expiry therefore makes Muster chattier, not safer.

**Crash recovery reads a journal, and the journal holds whole accounts, not ids.** Preferred
presence outlives the process that set it, so `presence-applied.json` under `%LOCALAPPDATA%` is
what `RecoverAsync` clears at startup. Storing ids would make recovery depend on the config still
describing them, and an account edited or deleted while the app was dead is exactly the one left
showing Busy. The journal swallows every failure it meets: a presence write must never fail because
the note about it could not be filed.

**Graph presence goes to `/me/presence/...`, not SPEC's `/users/{userId}/presence/...`.** Delegated
`Presence.ReadWrite` only ever permits the signed-in user, and the token already names them, so
`/me` removes the one way this code could write to the wrong person's status. The UPN in config is
still what picks the right token out of the MSAL cache.

**`Muster.Graph` is plain `net10.0`, so MSAL's interactive flow is the system browser.** That is the
right default rather than a limitation: consent for a client tenant often needs an admin, and the
system browser is where their session already is. Do not add a Windows target to reach the embedded
webview without a better reason than preference.

**Sign-in is interactive; a presence write is silent-only.** `GraphPresenceStrategy` never calls
`SignInAsync`. A consent window appearing because a meeting started would arrive with no
explanation and look exactly like a phishing prompt, so sign-in belongs to the settings screen,
where the user asked for it. A write with no cached token logs and gives up.

**The `page` strategy is unsupported, undocumented, and the only presence that actually works
here.** Every client tenant blocks consent for a third-party app registration, so `graph` is
theory and `page` is practice. It calls Teams' own `/ups/` presence service as the signed-in tab,
using that tab's own credentials — the same request the Teams UI makes when you change your status
by hand. It is *not* DOM automation and must not drift into it: nothing reads or clicks the Teams
UI. Four things about it are load-bearing, and none were guessable:

- **Region and version are captured, never constructed.** `/ups/apac/v1/` is this tenant's; a
  hardcoded `apac` fails silently for a client homed elsewhere. `TeamsPresenceTracker.BaseOf`
  derives it from whatever URL was actually seen, which covers `v1` becoming `v2` for free.
- **Releasing is a PUT with an empty body.** `{"availability":"Available"}` is not the same thing
  and is worse than doing nothing: it pins Available, so you show available during a genuine
  meeting in that tenant.
- **There is no expiry.** Graph's `expirationDuration` has no counterpart, so `PresenceApplier`'s
  journal and clear-on-startup stop being a backup and become the only protection against a crash
  mid-call. Do not weaken either on the grounds that the expiry will catch it.
- **Every set is read back.** A 2xx means accepted, not applied, and for an undocumented route
  accepted-but-ignored is exactly how it will fail when Microsoft changes it. The read-back matches
  on MRI rather than taking the first entry, because `getpresence` also serves colleagues.

**A captured token never leaves memory, and `TeamsPresenceSession.ToString` is overridden to keep
it that way.** The compiler-generated record `ToString` prints every property, so one careless log
line would put a live bearer token in a file the user is encouraged to open and share. The same
reasoning is why `probe-presence.js` and the host-side probe report header *names* only.

**`presenceStrategy: "dom"` is reserved in the schema and rejected by the validator.** It is the
fallback for a tenant that will not consent, and it is not built. Rejecting rather than ignoring is
deliberate: presence failing to apply is invisible by nature, so a config asking for a strategy
that quietly does nothing is the worst of both. `ServiceEditorViewModel.Strategies` leaves it out
of the settings picker for the same reason.

**Always pass `expirationDuration` when setting preferred presence.** Preferred presence does not auto-revert. Without an expiry, a crash mid-call leaves the user showing Busy across every client tenant indefinitely. `PT2H` is the floor.

**Preferred presence is a no-op without an active presence session.** If the Teams tab is signed out, the call silently does nothing. Check and log rather than assuming success.

**Handle `NewWindowRequested` and never fall through to the system browser *by default*.** A link
opening in the default browser means the wrong identity, which is the exact problem this app exists
to solve — while the default browser is an ordinary one. Where the registered http/https handler is
a router that picks a browser profile per URL, that argument inverts: going out to Windows is how
the link reaches the right identity. `links.external` is the switch, `auto` is the default, and it
answers no unless `ProfileRoutingHandler` recognises the handler. Two cases are never external
whatever the setting says — a scripted popup, because sign-in completing in another browser leaves the tab
waiting on a `window.opener` that never reports back, and a URL matching a pinned service, because
that tab is already signed in as the right identity.

**Recognising the handler needs both its ProgId and its executable, and neither alone.** The
upstream URL Router source registers `UrlRouterURL`; the build actually installed here registers
`DataByteUrlRouterURL` and lives under `%LOCALAPPDATA%\DataByte\UrlRouter`. A single hardcoded
ProgId was therefore wrong on the first machine it met, which is why `ProfileRoutingHandler` keeps
a list and also matches on the executable name behind `shell\open\command`. The ProgId is stable
but branded; the executable survives rebranding but is trivially imitated, so a match on either is
the compromise. Matching by name at all is what Windows leaves you: the `UserChoice` hash cannot be
verified or forged by an application.

**`UserChoice` is often simply absent, and that is not an error.** Reading only
`HKCU\...\UrlAssociations\https\UserChoice` reports "no handler" on a machine that plainly has
a browser — this one included, where no UserChoice exists for http or https and the effective
handler comes from the `https` class registration. So `ExternalLinkOpener` resolves the way the
shell does: UserChoice ProgId, then that ProgId's `shell\open\command`, then
`HKCR\https\shell\open\command`. A registry read that fails answers null rather than throwing,
and under `auto` null means no — links stay in a tab, which is the visible, harmless direction to
be wrong in.

**Route on the Safe Links destination, and navigate the wrapper.** Where a tenant has Defender Safe
Links on, every link in a Teams message is rewritten to
`https://{tenant}.safelinks.protection.outlook.com/?url=...`, so all of them present the same host
to `NewWindowRouter` and a link to a service pinned in that very workspace matches nothing at all —
it opens a second copy of it in a throwaway tab, signed out. `SafeLinks.Unwrap` fixes the match.
It is deliberately not applied to the URL that gets navigated or handed to Windows: unwrapping on
the way out opts the user out of a scan their tenant turned on.

The interstitial host is the part that will rot. Teams serves it from a CDN, not the safelinks
host, and *which* CDN has already changed: the documented one is `statics.teams.cdn.office.net`,
while what this tenant actually produced on 2026-09-02 was
`teams.public.onecdn.static.microsoft/evergreen-assets/safelinks/2/atp-safelinks.html`. Both are
in `SafeLinks.TeamsInterstitialHosts` and there will be more. The symptom of a missing one is a
link opening in an ephemeral tab named after a CDN host, which is what the log showed before this
existed. Its `url` parameter is the destination; its `dest` parameter is the reputation service
the interstitial itself calls, and reading that one would route every link to Microsoft.

**Muster drops a URL passed on the command line.** `App.OnStartup` reads `--config` and `--verbose`
and nothing else, and a second instance only ever sends `activate` down the pipe. So there is no
loop to fear if the external handler is ever pointed back at `Muster.exe` — but there is also no
way to route a link *into* the right workspace, which is the obvious next thing to want and is not
built.

**`UseWindowsForms` alongside `UseWPF` adds global usings that collide with WPF** on `Application`, `Panel`, `Color` and `KeyEventArgs`. `Muster.App.csproj` removes them with `<Using Remove="System.Windows.Forms" />` and `<Using Remove="System.Drawing" />`; only `Tray/TrayIcon.cs` imports WinForms, under a `Forms` alias. WinForms is there for `NotifyIcon` and nothing else.

**Windows 11 hides new tray icons behind the chevron by default.** Close-to-tray therefore looks exactly like the app quitting, so the first time the window hides it shows a balloon saying where it went. Do not remove that without replacing it with something equally visible.

**There are two unread badges, and they are not interchangeable.** `TrayIcon` replaces the whole
notification-area icon, so it paints the app icon and then the count over it, in GDI, with an
`HICON` to destroy by hand. `TaskbarBadge` sets `TaskbarItemInfo.Overlay`, which Windows
composites over the app icon itself, so it draws *only* the badge, in WPF, with a frozen
`ImageSource` and nothing unmanaged to leak. Both are fed from `MainViewModel.TotalUnread` in one
place. The taskbar one vanishes with the window's taskbar button when it hides to tray — that is
the case the tray badge exists for.

**The icon set is a runtime choice everywhere except on the exe.** `appearance.iconSet` repaints
the tray icon, every window's icon and the toast sender, all through `AppIcons` and its `Changed`
event — but `ApplicationIcon` in `Muster.App.csproj` is compiled into the executable, so Explorer,
a pinned taskbar shortcut, and any window not given an `Icon` explicitly keep showing the default
mark.

**A pinned taskbar button ignores the running window, and embedding icons in the exe is not the
fix.** It draws from the shortcut's icon location, written as `Muster.exe,0` when the pin is made,
so it is the thing still showing the old mark when everything else has changed. A `.lnk` can point
at any `.ico`, though, so `PinnedShortcut` repoints it at the file beside the exe rather than
anything being rebuilt — reachable only from the Appearance button, because it writes into the
user's taskbar folder. It matches the pin on target path against `Environment.ProcessPath`, so a
dev build reports "not pinned" while the pin names the deployed exe, which is correct and will
look like a bug the first time. Write it with `WScript.Shell` and read the link back: a `.lnk`
rewrite that silently drops fields is the failure mode.

**Editing the pin does not redraw the taskbar, and nothing short of restarting Explorer will.**
Measured on 2026-09-10: link pointing at the new `.ico`, window `ICON_BIG`/`ICON_SMALL`/`ICON_SMALL2`
all reading back as the new mark, and the button still drew the old one — through
`SHCNE_UPDATEITEM`, `SHCNE_ASSOCCHANGED` and `ie4uinit -show` alike. The taskbar keeps a pinned
item's icon in its own state and re-reads it when Explorer restarts. Do not go looking for a
notification that fixes this; there is not one.

**So repinning is the remedy, not the hazard — but only because of `RelaunchProperties`.** A repin
discards the shortcut and builds a new one, which is why it reverted to the compiled-in mark
before those properties existed. `RelaunchCommand` and `RelaunchIconResource` go on the shell
window in `OnSourceInitialized` and on every set change, and they are what the taskbar builds the
replacement shortcut from. Leave `RelaunchDisplayNameResource` alone — it wants a resource
reference, not a literal, and a bad one costs the pin its name where omitting it falls back to the
exe's `FileDescription`. Two mechanisms, two halves: the button repairs an existing pin, the
properties shape the next one.

**`InitPropVariantFromString` is not an export.** It is inline in `propvarutil.h`, so importing it
from `propsys.dll` builds cleanly and throws `EntryPointNotFoundException` at the call — which,
inside a best-effort `try`, means a silently dead feature. Build the `PROPVARIANT` by hand:
`VT_LPWSTR` plus `Marshal.StringToCoTaskMemUni`, which is what `PropVariantClear` frees. That is why `MainWindow`, `PopupWindow` and `SettingsWindow` are each handed one: a window
left to inherit is the one that visibly disagrees with the setting. Adding a set means a palette in
`assets/render.py`, a `Content` line in the csproj, an `IconSet` member, a case in
`AppIcons.FileNameOf` and a row in `AppIcons.Catalogue` — the validator rejects a name that has no
member, deliberately, for the same reason it rejects `presenceStrategy: "dom"`, but a *missing*
`FileNameOf` case fails the other way and silently serves the default. **Draw the artwork with
`render.py`, never by hand:** it holds the two optical variants (a thicker ring below 32px) and
reproduces both the shipped `muster.ico` frame for frame and the shipped mark SVGs byte for byte,
so a set drawn any other way will not match the ones beside it. Its SVGs deliberately diverge from
its rasters in one place — a bone-ground set gets a hairline slate keyline in vector only, because
a near-white tile needs an edge on a white page and has one already on a taskbar.

**A taskbar overlay is square, and anything wider is clipped rather than scaled.** Sizing the
badge to its text works up to two characters and then silently loses the ends of "99+" along with
both edges of the border. `TaskbarBadge` keeps the badge square and fits the type to it instead,
measuring rather than guessing from the character count. Render it to a PNG and look at it before
believing any change here.

**`Icon.FromHandle(bitmap.GetHicon())` leaks** until the handle is passed to `DestroyIcon`. `TrayIcon` destroys the previous badge each time it swaps one in.

**`VelopackApp.Build().Run()` must be the first statement in `App.OnStartup`,** ahead of
`base.OnStartup` and of the logger. Velopack's install, update and uninstall hooks arrive as
switches on this same exe and exit the process once serviced; a hook that reached the
single-instance claim or the config loader on the way past would be doing real work during an
uninstall.

**The Velopack package declares `shortcutAumid = velopack.Muster`, and `ToastService` does not use
it.** Toasts are identified by the toolkit's AUMID keyed on the exe path, and in a hand-copied
install there is no Start Menu shortcut to disagree with — an installed one creates exactly that.
If toasts stop appearing after installing rather than xcopying, this is the first place to look:
`VelopackApp.SetAppUserModelId` sets the process id, and it has to be changed together with the
registration in `ToastService`, never one alone.

**Velopack was chosen over Squirrel because the installed exe path does not move.** Squirrel's
versioned `app-x.y.z` folders would change `Environment.ProcessPath` on every update, which would
break the pinned-shortcut icon and reset the exe-path AUMID at the same time. Any future change of
installer has to preserve a stable exe path or knowingly re-solve both.

**`Muster.App` targets `net10.0-windows10.0.19041.0`, not plain `net10.0-windows`.** The toast APIs (`ToastNotificationManagerCompat` and its activation args) only exist in the notification package's Windows-targeted assets, and NuGet will not select them without a Windows SDK version. Consequence: build output lives in `bin/Debug/net10.0-windows10.0.19041.0/`. Launch from there — a stale `bin/Debug/net10.0-windows/` folder will silently run old code.

**Toasts from an unpackaged app are identified by an AUMID keyed on the exe path,** which the toolkit registers on first `Show()` with an empty `DisplayName`, so Windows labels every toast with the full path. `ToastService` fills in the name and icon *after* the first `Show()`; doing it before is wasted, because registering rewrites the key.

**`await using` needs `ConfigureAwait(false)` on the stream, not just on the read.** The implicit
`DisposeAsync` is an await too, and it captures whatever context the caller was on. `App.OnStartup`
blocks the UI thread on `LoadAsync` to get the logging section before there is a logger, so a
captured dispatcher there is a deadlock — and on a first run, where the missing file sends `Load`
through `SaveAsync`, it was one. It presented as the app starting with no window, no log line at
all, and a lone `muster.json.tmp` beside the config: indistinguishable from a hard startup crash,
and it hit every genuinely new install as well as the `--config <throwaway>` workflow this file
recommends. Fixed 2026-09-15 by binding the stream with `stream.ConfigureAwait(false)` on both
paths.

**Never reload the config through a missing file.** `IConfigStore.LoadAsync` writes the defaults
when the file is not there, which is right on first run and destructive during a watch: some
editors delete and recreate rather than writing in place, and a reload landing in that gap would
overwrite the user's config with defaults. `ConfigWatcher` checks `File.Exists` first and waits
for the next event.

**A config save arrives as a rename, not a change.** `JsonConfigStore.SaveAsync` writes a `.tmp`
beside the file and moves it into place, so a `FileSystemWatcher` filtered to `muster.json` sees
`Renamed`; hand edits produce `Changed`, `Created`, or two or three of them in a row. Subscribe to
all of them and debounce, or one save reloads the shell three times. Reading can still lose the
race with the writer, so retry on `IOException` before reporting the file as broken.

**Whether a config change costs a session is `ConfigDiff`'s call, and it is not obvious.**
Profile name and permission origins are fixed when a session's controller is created, so changing
either means tearing that session down; a changed URL is a navigation, which keeps the cookie jar
and the sign-in. Getting that backwards silently costs a sign-in, which is why the rules are unit
tested rather than reasoned about at the call site.

**`bridge.js` must never throw uncaught.** Wrap every shim in try/catch. A broken wrapper that breaks Teams is far worse than a missing notification. It is copied to `Assets/inject/bridge.js` beside the exe and read at startup; it posts before delegating to the original function, so a notification is still reported even when the underlying call is rejected for want of permission.

## Scope discipline

This project's reference point, Ferdium, became hard to maintain because it supports 100+ services through hand-written DOM-scraping recipes across three platforms. We are avoiding that specific failure.

Do not add, and push back if asked to add:

- Any platform other than Windows
- A plugin or recipe system. Services are config entries, not code.
- Per-service DOM scraping for anything other than Teams
- Browser features: history, bookmarks, download manager, extensions
- Telemetry, crash reporting, sync

**Auto-update was on that list and was granted an explicit exception** on 2026-09-10, when the
project moved to GitHub. It is Velopack against GitHub Releases and is kept deliberately small:
one source, no channels, no background timer, no silent restart. It checks at launch and when the
About screen asks, and applies on the next start. Do not grow it into a release-channel system
without the same explicit decision, and do not read the exception as softening the rest of the
list.

Teams is the only service that gets bespoke logic, because it is the only one where presence sync applies. Everything else uses the generic strategy: load a URL, parse the title for a count, intercept notifications.

## Testing

- `Muster.Core` gets real unit tests, especially the presence state machine. Cover: debounce on acquire, debounce on release, overlapping calls in two sessions, source session excluded from Busy, clear on return to Idle.
- `MicSignalMonitor` too, and for the same reason the presence machine gets it: it is a state
  machine over untrusted page input. Cover muted versus quiet versus live, sound holding the meter
  live through the gaps between words, two frames disagreeing, clamping a level from the page, and
  forgetting a session.
- `ConfigDiff` gets the same scrutiny: every change that must *not* disturb a live session, and every one that must.
- Config load/save round-trip tests with a golden file
- No UI tests. Manual verification for the shell.
- Graph client tested against a stubbed `HttpMessageHandler`, never against a live tenant in CI.
  It lives in `Muster.Core.Tests` rather than a project of its own: a test that can change
  someone's real status is not a test, and there is no second place for that rule to be forgotten.
- `PresenceApplier` gets the same scrutiny as the state machine. Cover: who is spared, a session
  joining mid-call, an account removed while Busy, presence switched off mid-call, a failed set not
  being recorded as applied, a failed clear staying applied so it is retried, and recovery from a
  journal the config no longer matches

## Verification before touching presence

Presence changes affect real status visible to real clients. Before changing anything under `Muster.Core/Presence` or `Muster.Graph`:

1. Run the presence state machine tests
2. If behaviour changed, exercise it with `presence.enabled: false` and the on-screen indicator only, before letting it write to Graph

## Secrets

Never write tokens, client secrets or refresh tokens to `muster.json`. MSAL cache only, DPAPI-protected under the current user. Never log token values, and redact `Authorization` headers in any HTTP logging.
