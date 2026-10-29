# VMP Launcher

A small, free Windows launcher that installs the host's exact modded KSP multiplayer setup from a tiny pack file
instead of a multi-gigabyte zip, keeps it verified against the server, and joins the VMP server with one click.

- **Exact versions, always.** A signed *lock* pins every mod by identifier, exact version, metadata hash and
  archive SHA-256. The launcher builds a private CKAN repository from the lock, so CKAN's own resolver cannot pick
  anything else, and it checks the result against the lock after every install.
- **Real CKAN, in-process.** The CKAN core library (MIT) is compiled in; mods download from their upstream sources.
- **Your KSP stays untouched.** The launcher creates a separate profile seeded from your install (hard-linked when on
  the same drive, so stock files cost no extra space).
- **Safe to interrupt.** Every install, update or repair is journaled with a hard-link snapshot and rolls back on
  failure or cancel. Updates download only what changed. Repair restores single damaged files from cached archives.
- **Multiplayer-ready.** Live server status (real VMP handshake probe), a Connection Doctor with plain-language
  fixes, pre-flight mod-policy check (the same DLL hashes the server enforces), auto-join on Play.
- **Works behind CGNAT.** Friends only need outbound UDP; the host's Playit tunnel (free tier) does the rest, with
  ZeroTier as a fallback.
- **Free and light.** Framework-dependent build (~15 MB installed), auto-updates via Velopack + GitHub Releases.
- **A full mod manager too.** Browse all ~3,800 CKAN mods with pictures and descriptions from SpaceDock, install,
  remove and update any of them, keep several KSP setups side by side, and share a setup with a code or a file.

## Mod manager

The **Mods** page works like a cleaner CKAN:

- **Setups.** The pack's own copy (browse-only: its mods match the server), plus as many others as you like: a
  *fresh copy* of KSP (stock files hard-linked from your install, so it costs almost no space) or *a KSP folder you
  already have* (Steam, or one CKAN manages: both tools read and write the same CKAN mod list). **Play** starts KSP
  from the selected setup. Removing a launcher copy deletes it; removing your own folder only makes the launcher
  forget it.
- **Browse.** Search by name, author or what a mod does; filter to compatible, new, already downloaded or everything;
  sort by popularity, recent updates, name or size. Each mod has a page with its SpaceDock picture, description and
  screenshots, links, what it needs, recommends or conflicts with, and its versions with changelogs.
- **Change.** Queue installs, removals and updates, then **Review**: CKAN's resolver shows every dependency it adds,
  every mod that has to go with a removed one, conflicts, recommended extras, and asks you to pick when several mods
  can provide something (e.g. Scatterer's sunflare). Everything is applied as one change: if a download or install
  fails, or you cancel, the setup stays exactly as it was. Archives already in your CKAN cache are reused.
- **Share.** **Share** gives a `VMPSHARE1-…` code (fits a chat message for small setups) and a `.vmpack` file; your
  friend pastes the code into **Import** or double-clicks the file. They see what is inside, get a new copy of their
  KSP with the same mods at the same versions, and review before anything installs. Only mods from CKAN are shared.

The CKAN mod list is the launcher's own copy (refreshed every few days, or with the refresh button); your own CKAN's
settings, instances and cache are only read, never changed. The full list takes about 0.5–0.8 GB of memory while the
Mods page has been opened.

## For friends

1. Download `VmpLauncher-win-Setup.exe` from the latest GitHub release. Windows SmartScreen may show
   "Windows protected your PC" because the EXE is not code-signed (certificates cost money): click
   **More info → Run anyway**. The .NET 10 Desktop Runtime is installed automatically if missing (one UAC prompt).
2. Run the launcher. It finds your KSP (1.12.5 with Making History and Breaking Ground), asks where the modded copy
   should live, and takes your player name and the server password (saved only on your PC).
3. **Install.** About 8.7 GB of mods are downloaded from their upstream sources and verified. If the host sent you an
   **invite code**, paste it once (setup wizard, Home or Settings): it fills in the server password and lets the
   launcher download the host's private mods (e.g. ApexGuidance), and every later version of them, by itself. Without
   an invite, drop the private zip the host sends you onto the Home page.
4. **Play.** Pick a route (Playit works from anywhere), press Play, KSP starts and joins the server.

See [docs/FRIEND-GUIDE.md](docs/FRIEND-GUIDE.md).

## For the host

```powershell
. .\eng\dotnet.ps1                       # pinned .NET 10 SDK on PATH
dotnet build VmpLauncher.slnx -c Debug
$vmpl = "src\VmpLauncher.Cli\bin\Debug\net10.0-windows\win-x64\vmpl.exe"

# one-time: signing key (set VMPL_KEY_PASSPHRASE first)
& $vmpl key generate --key-id vmp-host-2026a --owner Rjwolfe44

# one-time: where the host install, the packs repository and the game server are (stays in %APPDATA%, never in a repo)
& $vmpl publish-config --pack packs\vmp-train-career\vmp-train-career.vmpack.json `
    --ksp "C:\Games\Kerbal Space Program" --cache "$env:LOCALAPPDATA\CKAN\downloads" `
    --packs-repo ..\VMP-Packs --export-root out --policy "..\VMP\HOSTING\KSPModControl.xml" `
    --private-connection "..\VMP\HOSTING\connection.private.json" --key-id vmp-host-2026a `
    --server-host <ssh-alias> --server-root "C:\VMP Server" --local-server-config "..\VMP\HOSTING\VMPServer\Config"

# release a pack version: policy -> export -> verify -> stage -> push + server update
& $vmpl publish --pack packs\vmp-train-career\vmp-train-career.vmpack.json --notes "What changed"            # dry run: prints every external command
& $vmpl publish --pack packs\vmp-train-career\vmp-train-career.vmpack.json --notes "What changed" --execute  # asks you to type PUBLISH
& $vmpl verify-remote --pack-id vmp-train-career                                                             # what friends will see, incl. route probes

# private mods (one-time): a PRIVATE repo for your own mods + a read-only token that invite codes carry
gh repo create Rjwolfe44/VMP-Private-Mods --private
#   then on github.com: Settings > Developer settings > Fine-grained tokens > Generate:
#   repository access "Only select repositories: VMP-Private-Mods", permission "Contents: Read-only"
& $vmpl private set-token --repo Rjwolfe44/VMP-Private-Mods     # asks for the token without echo, checks it
& $vmpl private invite --repo Rjwolfe44/VMP-Private-Mods        # copies an invite code (token + server password)

# lower-level pieces, if you need them
& $vmpl capture ...            # export only (what publish does in step 2)
& $vmpl verify-export <dir>    # validate an export or staged version folder
& $vmpl policy update --policy ..\VMP\HOSTING\KSPModControl.xml --game "C:\Games\Kerbal Space Program"

# package the launcher
.\build.ps1 -Version 0.1.0            # artifacts\releases\VmpLauncher-win-Setup.exe (+ portable zip, nupkg)
.\publish-launcher.ps1 -Version 0.1.0 # EXTERNAL: uploads a draft GitHub release
```

The pack definition (`packs/<id>/<id>.vmpack.json`) lists the CKAN selection (by default: everything in the host's
registry), custom packages (local mods such as the VMP client or ApexGuidance, each either `github-release` or
`private-sideload`), file rules (what is generated/per-player/mutable), overrides, and server presets (no passwords).
`capture` classifies every file in the host GameData, refuses to ship unknown DLLs, scans every output file for the
host's real secrets, and writes the lock + inventory + assets.

`publish` is the whole release in one command. It re-hashes the server policy's DLL list against the host GameData
(a rebuilt mod is just a hash change; a mod added or removed stops the run and asks for a file generated in-game with
`--policy-generated`, because only the game knows the part list), checks the host passes that policy, exports and
signs the pack, verifies the export, stages it into the packs repository layout (version folder, channel pointer,
current policy, changelog entry from the lock diff, `servers.json` build bump, catalog/share/.gitattributes), and
verifies the staged copy again. Without `--execute` the staging goes to a scratch mirror and every external command
is printed verbatim. With `--execute` it refuses a dirty or stale repository, creates missing GitHub release assets
and checks their hash after upload, commits and pushes, uploads the policy to the server over SSH (checksum, backup,
stop, replace, start, confirm running) only when the policy changed, then fetches everything back from the public
URLs and probes the routes. The same step turns `ModControl` on in `Config/vmp-server.json`, or in
`GeneralSettings.xml` on servers that still use the older settings files.

The same flow is in the launcher itself on the host's PC: **Host tools** (shown only where a publisher key and a publish
configuration exist) runs the check, shows every action, and publishes after you type PUBLISH. It also makes invite
codes.

### Private mods

A custom package with `"distribution": { "kind": "private-github", "repo": "owner/private-repo", "tagPrefix": "apex-" }`
is uploaded by `publish --execute` as a release asset of that **private** repository (the upload refuses unless GitHub
reports the repository as private), then downloaded back and hash-checked. The signed lock records where it lives.
Friends' launchers that hold an invite code download it with the read-only token, verify it against the lock, and
keep it in a local private cache, so when you rebuild ApexGuidance and publish, friends get the new build with the
next pack update without asking you for a file. Older launchers and friends without an invite still see the
"drop the zip here" card. Any number of your own mods can share one private repository (one custom package each,
each with its own `tagPrefix`).

An invite code is a bearer secret: anyone holding it can download your private mods. Send it privately. To cut
everyone off, revoke the token on GitHub and send new invites. Never use your own login token (`gh auth token`) for
this; the launcher refuses classic and OAuth tokens. `private-sideload` (hand-over only) still works.

## Layout

| Project | Role |
|---|---|
| `src/VmpLauncher.Core` | pack/lock/inventory model, profiles, journaled transactions, hashing, signing, policy check |
| `src/VmpLauncher.Ckan` | adapter over the CKAN core submodule (`external/CKAN`, v1.36.4) |
| `src/VmpLauncher.Net` | VMP/Lidgren probes, signed server directory, ZeroTier inspection, Connection Doctor |
| `src/VmpLauncher.Game` | KSP detection, VMP settings.xml, client inspection, launch |
| `src/VmpLauncher.Publishing` | host-side capture, custom packages, secret scan, export |
| `src/VmpLauncher.Cli` | `vmpl`: every operation scriptable and testable without the GUI |
| `src/VmpLauncher.App` | WPF + WPF-UI launcher |
| `tests/` | unit tests (Core/Net/Game/App ViewModels) and CKAN-in-process integration tests with a fixture repo |

`docs/ckan-contract.md` records the CKAN behaviours the design relies on and how they were verified.

## Licenses

VMP Launcher: MIT. Bundled: CKAN core (MIT), WPF-UI (MIT), Velopack (MIT), Lidgren.Network (MIT),
CommunityToolkit.Mvvm (MIT), Microsoft.Extensions.* (MIT). Kerbal Space Program and its DLC are never redistributed.
