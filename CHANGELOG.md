# Changelog

## Unreleased

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
