# Changelog

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
