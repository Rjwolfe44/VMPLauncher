# Changelog

## 0.3.1

- Turning off Settings > Beta channel now really switches back to stable releases. Before, a launcher installed from a
  beta kept looking for betas only, found none among the stable releases and said it was up to date. A beta newer
  than the stable release stays installed until stable catches up (no downgrade).
- Stable releases are now also published to the beta channel, so beta testers get them too. This release reaches
  every launcher, including betas that were stuck with the switch off.
- An update that stops on DLLs the pack doesn't know (an extra mod dropped into the launcher's copy) now moves them to
  the copy's quarantine folder and tries again, like it already does for changed files.

## 0.3.0

The mod manager from the 0.3.0 betas is now on the stable channel, plus pack updates that need no clicks.

- **Mod manager.** Browse the whole CKAN catalog (about 3,800 mods) with pictures and descriptions, make extra KSP
  setups or manage a KSP folder you already have, install, remove and update any mod with a review before anything
  changes, and share a setup as a code or a `.vmpack` file. The pack's own copy stays exactly as the host published
  it. Details in the beta notes below.
- **New packs install by themselves.** The launcher checks for a new pack before Play, when KSP closes and when you
  come back to its window (at most every 10 minutes; a check is three small files), and installs it when KSP isn't
  running. Before, a launcher left open installed only the first new pack by itself and then waited for a click.
- **Updates repair themselves.** KSP redraws some part icons inside mod folders, which made the next update stop with
  "Installed files do not match the pack". An update that finds files changed since the install now puts them back
  from the downloads it already has and tries again.
- Right after the host publishes, GitHub can serve the new pack files with an old signature for a few minutes. The
  launcher now keeps the pack it has and checks again later, instead of showing a signature error.
- An unchanged pack is no longer downloaded again on every check.
- Host tools: publishing waits up to two minutes for GitHub to serve a newly uploaded mod file (it gave up after 27
  seconds). Part icons (`@thumbs` folders) are treated as files KSP may rewrite.

## 0.3.0-beta.3

- When SpaceDock (the site mod pictures and long descriptions come from) is down, the Mods page says so instead of
  just showing letter tiles. The launcher then checks it once a minute rather than for every row, and fills the
  pictures in by itself as soon as SpaceDock answers again; no restart needed.

## 0.3.0-beta.2

- Mod pictures load much faster. SpaceDock only serves full-size pictures (about 1 MB each), so each one is now
  downloaded once, shrunk to the size it is shown at and kept as a small file (about 3 KB for a list tile); after
  that it loads from disk instantly. The rows on screen load first, with up to 8 downloads at once.
- Mod pages reopen instantly, and switching between mods no longer reloads the setup's mod list from disk.
- Once you have used the Mods page, the launcher loads the CKAN mod list in the background shortly after it starts,
  so the page opens without the wait.

## 0.3.0-beta.1

First beta of the mod manager, on the beta channel only (Settings > Beta channel). Switching the channel now takes
effect without restarting the launcher.

- **Mod manager.** The Mods page now browses the whole CKAN catalog (about 3,800 mods) for any setup: search by name,
  author or description, filters (compatible, installed, updates, new, already downloaded, not for this KSP), tags and
  sorting, with SpaceDock pictures and descriptions on every mod's page.
- **Setups.** Make fresh KSP copies (hard links, almost no extra space) or manage a KSP folder you already have, and
  switch between them. Play starts KSP from any setup. The pack's copy stays browse-only so it always matches the
  server; "Make a custom copy" starts a free one.
- **Install, remove, update any mod.** Changes are queued and reviewed: dependencies, removals that cascade, conflicts,
  recommended extras and "pick which mod provides X" are all shown before anything changes, and the whole change runs
  as one CKAN transaction that rolls back on failure or cancel. "Update all" on the Updates tab.
- **Share and import.** Share a setup as a short code or a `.vmpack` file; importing one makes a new copy with the same
  mods at the same versions (or the newest one when a version left CKAN) and opens the review.
- Fix: a KSP started from one copy no longer blocks changes to another copy whose game files are hard links of the
  same install.
- CLI: `vmpl catalog update|list|show|plan|apply|seed` for the mod manager.

## 0.2.2

- Your downloads, game copy and settings now live in `%LOCALAPPDATA%\VmpLauncherData`, outside the program folder.
  Before, they shared the folder Windows Setup and uninstall replace, so reinstalling the launcher could delete them.
  Existing data moves over by itself the first time 0.2.2 starts (same drive, so it is instant).
- Play: "Just launch KSP (no server)" starts the game with the pack's mods without joining. Play single-player, or
  connect later from the VMP window.
- Home no longer cancels its own first pack check at startup.

## 0.2.1

- Mods you already have in a normal CKAN download cache are reused instead of downloaded again: matched by CKAN's file
  name, checked against the pack's hash, and hard-linked (no extra disk space on the same drive). A full install on a
  PC with CKAN's cache took 2 minutes and downloaded nothing.
- Settings: "Your CKAN download cache" shows its size and what is shared with the launcher; "Transfer the pack's
  mods" takes them over without copying, "Clear CKAN's cache" frees the rest (asks first, not while CKAN runs).
- Settings: "Clean up old downloads" removes archives neither the current pack nor your install still uses.
- Downloads show percent, speed and time left.
- Launcher updates install by themselves when you close the launcher; no restart click needed.
- The launcher opens on the main monitor, and Play starts KSP on the main monitor (Settings switch).
- Host tools: the token button opens GitHub's page pre-filled; publishing no longer shows on Home.

## 0.2.0

### Launcher UI (rebuilt)
- New shell: custom sidebar with a live server-status card, pack version and update pill; pages slide in; no more
  WPF-UI NavigationView (its auto-hiding scroll viewer swallowed the pages' own scrollbars).
- Every page scrolls with a visible scrollbar; long logs follow the newest line.
- Shikigami purple is the real accent now (buttons and toggles were rendering near-white).
- Home: hero with the install/update/play button and live progress inline, private-mods card with invite paste,
  fact tiles, requirements and "What's new" from the pack changelog. Pack updates apply automatically on launch when
  nothing needs the user (setting).
- Play: route cards with status, password saves on Enter, big launch button, the window restores when KSP exits.
- New Mods page (search and filters over the pack's mods), Activity page with last result, Doctor runs on first
  visit and names the steps that need attention, Settings has invite codes and private-repo access.
- Host tools page (host PC only): publish check and publish, invite codes.
- Installer splash redrawn (the old one showed "Loadingâ€¦" because its script was read with the wrong encoding).

### Private mods
- `private-github` custom-package distribution: private zips live in a private GitHub repository; invite codes give
  friends a read-only token (DPAPI-stored) so the launcher downloads and updates them automatically.
- `vmpl private set-token | check | invite`.

### Fixes from the code audit
- Transactions: a cleanup error after commit no longer rolls back a successful install; interrupted snapshots no
  longer leave the profile stuck; more rollback/repair edge cases (see the commit).
- Publishing: the server policy is compared with what the server runs (a failed SSH step is retried next time);
  existing release assets must hash to what the lock promises; rebuilt packages with an unchanged version are
  refused; published version folders are immutable; secret scan fails closed, reads `.gz` files and decodes inline
  override content, and also looks for the private-repo tokens; the server is backed up and stopped inside the
  try/finally that restarts it; `--skip-assets` refuses when downloads are missing; LAN presets stay off the repo.
- Network: a full server is "Online, but full" instead of "not a VMP server"; the Doctor warns when a password is
  needed but not saved; a damaged LmpClient.dll no longer breaks the Doctor; servers.json signature races retry with
  a cache-buster; rollback protection survives a bad cache; user names with spaces are redacted; ZeroTier port file.
- App: the activity log stopped updating after 200 lines and was read across threads; an install's cancellation
  token was disposed while the install was still running; the lock signature was deleted on a transient network
  error.

- `vmpl publish`: one-command pack release (policy re-hash from the host GameData, export, verify, stage into the
  packs repository, changelog, servers.json build bump; dry run by default, `--execute` pushes, creates missing
  release assets and updates the server policy over SSH with backup/stop/replace/start, then verifies the published
  files and probes the routes). `vmpl publish-config`, `vmpl verify-remote`, `vmpl policy update`.
- Custom-package metadata no longer carries an export timestamp, so an unchanged package is unchanged across exports.
- Lock diff no longer reports artifact-less modules (metapackages, DLC) as changed on every update.
- The exported pack definition carries the version actually built when `--version` overrides it.

## 0.1.0 - 2026-10-22

First end-to-end build.

- Signed, exact-version pack locks generated from a live KSP/CKAN install; CKAN core compiled in (v1.36.4).
- Managed profile seeded from the player's own KSP (hard links on the same drive).
- Journaled install/update/repair with hard-link snapshots and rollback; updates download only changed archives.
- Custom local mods as CKAN-shaped packages (GitHub release or private sideload).
- Host capture/export with file classification and secret scan.
- Live server status and Connection Doctor using a real VMP handshake probe; pre-flight mod-policy check.
- Auto-join on Play (needs VMP client build 0.2.0-train.20261022.1 or newer).
- WPF UI: dark space theme, Mica, first-run wizard, Home/Play/Activity/Doctor/Settings/About.
