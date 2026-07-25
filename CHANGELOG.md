# Changelog

The short, user-facing version of each release also lives in `manifest.json`, because that is the text
Jellyfin shows in the plugin catalog. This file is the longer version, and it is the one to edit first
when preparing a release. Older entries below are condensed from the manifest.

## Unreleased

Security fixes. Nothing changes about how you create, share or revoke a link, but one thing you could do
before is gone on purpose: the dashboard no longer offers a Copy or Open button per share.

- **Share URLs are no longer written to disk.** Up to and including 0.2.2 the full share URL was saved in
  `guestpass.json` so the dashboard could re-show it. That URL carries the raw token in its query string,
  so the store held a redeemable credential next to the HMAC hash that was supposed to replace it, and
  anyone able to read that file could open every live link. The URL is now returned once, in the response
  to creating the link, and never persisted. The README previously claimed the token was never stored,
  which was not true while that field existed.
- **Old stores are scrubbed at startup.** On the first start after upgrading, the plugin rewrites
  `guestpass.json` without the legacy `ShareUrl` field and logs how many records it cleaned. Existing
  links keep working, they simply cannot be read back out of the store any more. The scrub runs before
  the normal cleanup pass, so a cleanup failure cannot leave the tokens sitting there.
- **The dashboard link column now says so.** The Copy and Open buttons are gone, along with the clipboard
  helpers behind them, because the server has nothing left to hand back. The URL is still copied to your
  clipboard at the moment you create the link, which is the only moment it exists.
- **Guest creation fails closed on tag confinement.** Applying `AllowedTags` to the guest's user policy
  goes through reflection, so that Jellyfin renaming or dropping a policy member does not break the whole
  plugin. That tolerance was wrong for the members that do the confining: `AllowedTags` is what limits a
  guest to the shared item, `EnableAllFolders` is true by design, so a silently skipped write would have
  handed the guest the entire library. `AllowedTags`, `IsAdministrator` and `IsDisabled` now throw if they
  cannot be set, and a record with no tag is refused outright, so redemption fails instead of over-sharing.

Repository changes, no effect on the plugin itself:

- Added a build workflow that runs on every push to main and every pull request: restore, Release build,
  `dotnet format --verify-no-changes`, and a NuGet vulnerability check over direct and transitive
  packages. Until now nothing verified that main compiled, and that was only discovered at release time.
- Added a version gate to the release workflow. A tag is only released when it matches the version in
  `meta.json`, the csproj `Version`, `AssemblyVersion` and `FileVersion` all agree with it, the plugin guid
  in `manifest.json` matches `meta.json`, and the tagged commit is actually on main, which matters because
  the release is built from main rather than from the tag.

## 0.2.2

Two fixes. GuestPass no longer appears on non-media menus such as the Users dashboard: the entry is only
added after the server confirms the item is a movie, episode, series or season, instead of guessing from
menu ids. Opening a share link no longer logs you out: an existing working sign-in for the server is kept
and the shared title just opens in it, instead of the guest session overwriting your login and leaving you
on a black page once the link was revoked. If an older link left you stuck on a black page, clear the
site's storage or run `localStorage.clear()` in the console.

## 0.2.1

Hardening from a code review of 0.2.0. Deleting a link no longer removes its record when teardown failed,
so a failed cleanup stays visible and is retried instead of silently orphaning a guest user or tag. Guest
teardown finds the guest user by its deterministic name as a last resort, closing a window where a crash
or a delete racing a redemption could leave an enabled guest account behind. The guest session count is
capped instead of unlimited. Corrected the context-menu detection to jellyfin-web's real action ids.

## 0.2.0

Links now work every time until they expire or are revoked, instead of being one-time use, and work on
several devices at once. Adds a Delete action that revokes a link and removes its record, so revoked links
no longer pile up. The GuestPass entry no longer appears on the Users dashboard menu, only on movies,
episodes, series and seasons. Removes the stray French line on the dead-link page.

## 0.1.0

First GuestPass release. Fork of [jellyfin-plugin-sharelinks](https://github.com/Franciskid/jellyfin-plugin-sharelinks)
by [Franciskid](https://github.com/Franciskid), with the on-disk `index.html` rewrite replaced by in-memory
`IStartupFilter` injection, so the plugin works on containers whose web root is not writable. Adds an
injection status panel, a switch to disable injection without uninstalling, and context-menu matching for
non-English web clients including Dutch and German.
