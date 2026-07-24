# GuestPass for Jellyfin

Send someone a single movie, episode, season or whole series, without giving them an account, without
them seeing the rest of your library.

> *"here, watch this one film, the link dies tomorrow"*

GuestPass adds a **GuestPass** entry to the context menu of any movie, episode, series or season in the
Jellyfin web client. Pick how long the link should live and you get a URL you can send to anyone. When
they open it they land straight on that title, already signed in, and they cannot wander off into the
rest of your server.

No account for them to create, no password for you to hand out, no permanent guest user piling up. The
link is temporary, the guest is temporary, and when it expires everything is cleaned up on its own.

## Why this fork exists

The upstream plugin delivers its client script by **rewriting `index.html` on disk** when the server
starts. That fails on any container whose web root is not writable, which is most of them:

- the official `jellyfin/jellyfin` image with a non-root `PUID`
- linuxserver.io images
- TrueNAS SCALE apps, where the app schema pins the user id to a minimum of 2 so root is not selectable
- Kubernetes deployments with a read-only root filesystem

The write throws `UnauthorizedAccessException`, the exception is swallowed into a single log warning, and
the plugin presents as "installed, enabled, no button". Nothing else about it is wrong.

**GuestPass injects the script in memory instead.** It registers an ASP.NET Core `IStartupFilter` and
serves a modified `index.html` from the response pipeline. The file on disk is never touched, never even
opened for writing, so it needs no write access and no root, and it survives Jellyfin updates and
container rebuilds untouched. Reading the web root is enough, and that always works, since the server is
already serving those files to every client.

## How it works

1. As an admin you open the context menu on a movie, episode, series or season and hit **GuestPass**.
   Choose an expiry and the plugin hands you a link, copied to your clipboard.
2. The plugin tags the shared item with a unique random tag and records the share. Share a series or a
   season and the tag is applied to the whole tree underneath it, so the guest can browse from the series
   page down into a season and an episode. The raw link token is shown to you once and never stored, only
   a keyed HMAC hash of it is kept.
3. Whoever opens the link gets a throwaway guest user created on the spot, restricted by that tag to the
   shared item and its tree, and is signed in automatically.
4. When the link expires, or you revoke it, a cleanup pass disables and deletes the guest user and strips
   the temporary tag from the whole tree again.

## What the guest sees

Just the shared title, and the ability to play it. The confinement is enforced on the server, not only in
the browser: the guest's Jellyfin policy only permits items carrying the share's tag, so every other
movie, show, library and search comes back empty from the API. Even someone poking at the raw API cannot
list your other content. On top of that the web client is locked down for the guest, hiding navigation
and making in-page links inert.

## Managing links

The plugin's dashboard page lists every share with its status, title, a copyable link, the guest name and
an expiry, and lets you revoke any of them on the spot. Revoking runs the same teardown as expiry: guest
gone, tag gone.

## Install

Dashboard > Plugins > Repositories > **New repository**, then paste:

```
https://raw.githubusercontent.com/SurfHost/jellyfin-plugin-guestpass/main/manifest.json
```

Then Catalog > GuestPass > Install, and **restart Jellyfin**. The restart is required: `IStartupFilter`
instances are only collected when the server process starts, so the injection does not activate until
then.

## Compatibility

Jellyfin **10.11** (`targetAbi 10.11.0.0`), .NET 9.

## Credits and license

GuestPass is a fork of [jellyfin-plugin-sharelinks](https://github.com/Franciskid/jellyfin-plugin-sharelinks)
by [Franciskid](https://github.com/Franciskid), whose work is the entire foundation of this plugin. All
credit for the original design, the guest-user confinement model and the token handling belongs there.

Changes made in this fork by [SurfHost](https://github.com/SurfHost):

- replaced the on-disk `index.html` rewrite with in-memory `IStartupFilter` injection, so the plugin works
  on containers with a read-only or root-owned web root
- rebranded to GuestPass with its own plugin id, route and data directory, so it neither collides with nor
  depends on the original
- Dutch web client support
- injection status surfaced on the configuration page instead of only in the server log
- a configuration toggle to disable the injection without uninstalling

Licensed under the [GPL-3.0](LICENSE), like the original and like most Jellyfin plugins.
