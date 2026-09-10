# Muster

A Windows shell that keeps several isolated web sessions signed in at once, grouped into
workspaces, with Microsoft Teams presence kept in step across them.

It exists to replace running four or five Edge profiles side by side. If you work across several
Microsoft 365 tenants — your own and a handful of clients' — you end up with a window per tenant,
no idea which one a notification came from, and a status in each that knows nothing about the
others. Muster puts them in one window, keeps each on its own cookie jar, and makes a call in one
tenant show you as Busy in the rest.

> Windows only, and deliberately so. See [Non-goals](#non-goals).

## What it does

- **Workspaces.** One per identity. Every service inside a workspace shares that workspace's
  WebView2 profile, so the sign-in is shared within it and impossible to leak between them.
- **Tabs that stay alive.** Switching tabs changes visibility, never the URL. Background sessions
  keep loading, keep their sign-in, and keep delivering notifications.
- **Unread badges** on tabs, workspaces, the tray icon and the taskbar button, parsed from the
  page title.
- **Notifications** intercepted from the page — including the service-worker notifications the
  WebView2 API does not surface — shown as native Windows toasts and kept in a panel.
- **Presence sync.** A call in one tenant sets Busy in the others, and clears when it ends. The
  tenant you are actually talking in is spared.
- **Suspension.** Workspaces you are not looking at sleep after an idle window and hand back
  their memory. Anything marked `keepAlive`, and anything holding a microphone, never does.
- **Link routing.** A link to a service you have pinned opens in that service's tab, signed in as
  the right identity. Everything else can stay in the shell or go out to Windows, depending on
  whether your default handler routes by profile.

## Requirements

- Windows 10 1809 or later
- The [WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/), which is
  already present on any up-to-date Windows 11
- The .NET 10 desktop runtime, if you build from source rather than using a release

## Install

Grab the latest installer from [Releases](https://github.com/jamtheradar/Muster/releases). It
installs per-user, needs no administrator, and updates itself from the same place — Muster checks
on launch and applies the update the next time you start it.

## Configuration

Everything lives in one hand-editable file, `%APPDATA%\Muster\muster.json`, and every field in it
is also editable from Settings. The file stays the source of record: the settings screen writes
the same shape a person would type, through the same validator, and the app reloads the file when
it changes on disk.

```json
{
  "version": 1,
  "workspaces": [
    {
      "id": "acme",
      "name": "Acme",
      "accent": "#2D7D9A",
      "services": [
        {
          "id": "teams-acme",
          "name": "Teams",
          "kind": "teams",
          "url": "https://teams.cloud.microsoft/",
          "keepAlive": true,
          "notificationsEnabled": true,
          "presence": { "strategy": "page" }
        }
      ]
    }
  ],
  "presence": { "enabled": false, "expirationDuration": "PT2H" },
  "appearance": { "iconSet": "slate" },
  "links": { "external": "auto" },
  "suspension": { "enabled": true, "idleMinutes": 5 }
}
```

A few things worth knowing:

- **`id` picks the profile folder.** Renaming a workspace id orphans its WebView2 profile and
  costs you the sign-in. Change `name` freely; leave `id` alone.
- **`presence.enabled` defaults to false**, and should stay false until you have read
  [Presence](#presence). Turning it on changes status that real people can see.
- **`appearance.iconSet`** is one of `slate`, `rimmed`, `inverted`, `bone`, `ochre`, `eucalypt`,
  `clay`, `hiVis`. Settings shows the artwork rather than the names.

Run against a throwaway config with `Muster.exe --config <path>` rather than editing the real one.
`--verbose` turns on debug logging for the run.

## Presence

Two strategies, and they are not equally useful.

- **`page`** calls Teams' own presence service as the signed-in tab, using that tab's own
  credentials — the same request the Teams UI makes when you set your status by hand. Nothing to
  register, nothing to consent to. It is undocumented, and it is the one that works.
- **`graph`** uses Microsoft Graph with a delegated `Presence.ReadWrite` token. It is the
  supportable route and it needs an admin in each tenant to consent to your app registration,
  which in practice most client tenants will not do.

It is **not** DOM automation. Nothing reads or clicks the Teams UI, and there is no per-service
scraping — see below.

Presence writes always carry an expiry, because preferred presence does not revert on its own and
a crash mid-call would otherwise leave you Busy everywhere indefinitely. Muster also keeps a
journal of what it has applied and clears it at startup.

## Non-goals

This project's reference point, Ferdium, became hard to maintain by supporting 100+ services
through hand-written DOM-scraping recipes across three platforms. Muster avoids that specific
failure by refusing the things that lead to it:

- Any platform other than Windows
- A plugin or recipe system — services are config entries, not code
- Per-service DOM scraping for anything other than Teams
- Browser furniture: history, bookmarks, downloads, extensions
- Telemetry, crash reporting, sync

Teams is the only service with bespoke logic, because presence is the only thing that needs it.
Everything else uses the generic path: load a URL, parse the title for a count, intercept
notifications.

## Building

```
dotnet build Muster.slnx
dotnet test Muster.Core.Tests/Muster.Core.Tests.csproj
```

To produce an installer, bump `<Version>` in `Directory.Build.props` and run:

```
dotnet tool restore
./build/pack.ps1
```

That runs the tests, publishes framework-dependent, and packs with
[Velopack](https://velopack.io) into `releases/`. Upload the whole folder to a GitHub release
tagged `v<version>`; the installed app reads that release list to update itself. The installer
brings the .NET 10 desktop runtime and the WebView2 runtime with it, so a fresh machine needs
neither in advance. Nothing is code-signed, so SmartScreen will warn on first run until a
certificate is added.

| Project | |
| --- | --- |
| `Muster.App` | The WPF shell. Views, view models, injected JS, tray, toasts. |
| `Muster.Core` | Domain: config, sessions, notifications, presence, routing. Plain `net10.0`. |
| `Muster.Graph` | Graph presence client and MSAL token cache. Plain `net10.0`. |
| `Muster.Core.Tests` | xunit. Config round-trip, presence state machine, routing, Graph client. |

`Muster.Core` targets `net10.0` rather than `net10.0-windows` on purpose, so that a stray WPF or
WebView2 reference is a compile error rather than a code review comment. That is what keeps the
presence state machine and the notification pipeline testable without launching a browser.

## Further reading

[`CLAUDE.md`](CLAUDE.md) is the accumulated "things that will bite you" — the WebView2 behaviours,
the presence constraints and the Windows shell traps that are not guessable from the code. Read it
before changing anything.

The design document and the running build status live outside the repo, under `.claude/docs/`.

## Licence

[MIT](LICENSE). Copyright (c) 2026 James Noonan.
