# Changelog

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
