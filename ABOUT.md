# About Muster

Muster is a Windows shell for people who work inside several Microsoft 365 tenants at once.

## The problem it was built for

Consulting across client tenants means an identity per client. The web apps that go with them —
Teams, the Azure portal, SharePoint, a DevOps organisation — will not share a browser profile,
because signing into one signs you out of the last. The usual answer is a browser profile per
client and a window per profile, which produces three problems at once:

1. **You lose track of which identity a window is.** They look identical. Acting in the wrong
   tenant is easy and occasionally expensive.
2. **Notifications arrive with no attribution.** A Teams toast tells you someone messaged. It
   does not tell you which of five Teams instances raised it.
3. **Presence is per tenant and knows nothing.** In a call for one client, you show Available to
   every other one, and get interrupted accordingly.

Muster answers all three with one window: workspaces that are visibly distinct, notifications
attributed to the workspace that raised them, and presence reconciled across every tenant you
have configured.

## What it is not

It is not a browser, and it is not a general-purpose service aggregator. There is no history, no
bookmark manager, no download manager, no extensions. There is no plugin system and no recipe
format — a service is four lines of JSON, never code.

That restraint is deliberate and it is the main design constraint on the project. The closest
comparable tool, Ferdium, supports over a hundred services through hand-written DOM-scraping
recipes across three platforms, and the cost of keeping those recipes working against other
people's UI changes is most of the maintenance burden it carries. Muster scrapes exactly one
thing — the Teams call state, because presence needs it — and everything else goes through a
generic path that cannot rot in the same way: load a URL, read the title for an unread count,
intercept the notification API.

The same reasoning rules out telemetry, crash reporting and sync. Nothing Muster knows about your
tenants leaves the machine, and the log file is local, plain text, and yours.

## How it works, briefly

Each workspace owns a WebView2 profile directory, so isolation is the browser engine's job rather
than something Muster has to enforce. Each service inside a workspace is its own long-lived
WebView2 control; switching tabs toggles visibility and never touches the URL, which is what lets
a background service stay signed in and keep delivering notifications.

A small injected script wraps the page's notification and media APIs — including the
service-worker notification path the WebView2 host API does not surface — and reports back as
plain events. The domain layer sees those events and nothing else: it has no reference to WPF or
WebView2 at all, which is what makes the presence state machine and the notification pipeline
testable without launching a browser.

Presence is computed rather than commanded. One component decides which accounts *should* be
Busy and reconciles that against which ones already are, so the awkward cases — a session joining
a call late, an account deleted from the config while it is showing Busy — fall out of the design
instead of needing to be handled.

## Status

Every milestone in the design document is built. The parts that are unverified by hand, and the
reasons, are recorded honestly rather than glossed — the design and the running status live with
the project's working notes rather than in this repository.

## Credits and licence

Built by James Noonan. Released under the [MIT licence](LICENSE).

Muster stands on WebView2, .NET and the Windows Community Toolkit. It is not affiliated with,
endorsed by, or supported by Microsoft, and "Microsoft", "Teams" and "Microsoft 365" are
Microsoft's trademarks.
