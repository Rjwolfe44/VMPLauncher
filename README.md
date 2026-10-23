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

## For friends

1. Download `VmpLauncher-win-Setup.exe` from the latest GitHub release. Windows SmartScreen may show
   "Windows protected your PC" because the EXE is not code-signed (certificates cost money): click
   **More info → Run anyway**. The .NET 10 Desktop Runtime is installed automatically if missing (one UAC prompt).
2. Run the launcher. It finds your KSP (1.12.5 with Making History and Breaking Ground), asks where the modded copy
   should live, and takes your player name and the server password (saved only on your PC).
3. **Install.** About 8.7 GB of mods are downloaded from their upstream sources and verified. Private packages the
   host hands you (e.g. ApexGuidance) are dropped onto the Home page.
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
    --ksp "C:\Users\rjwol\Desktop\Kerbal Space Program" --cache "$env:LOCALAPPDATA\CKAN\downloads" `
    --packs-repo ..\VMP-Packs --export-root out --policy "..\VMP\HOSTING\KSPModControl.xml" `
    --private-connection "..\VMP\HOSTING\connection.private.json" --key-id vmp-host-2026a `
    --server-host trainpc --server-root "C:\Users\The Strip\Desktop\VMP Server" --local-server-config "..\VMP\HOSTING\VMPServer\Config"

# release a pack version: policy -> export -> verify -> stage -> push + server update
& $vmpl publish --pack packs\vmp-train-career\vmp-train-career.vmpack.json --notes "What changed"            # dry run: prints every external command
& $vmpl publish --pack packs\vmp-train-career\vmp-train-career.vmpack.json --notes "What changed" --execute  # asks you to type PUBLISH
& $vmpl verify-remote --pack-id vmp-train-career                                                             # what friends will see, incl. route probes

# lower-level pieces, if you need them
& $vmpl capture ...            # export only (what publish does in step 2)
& $vmpl verify-export <dir>    # validate an export or staged version folder
& $vmpl policy update --policy ..\VMP\HOSTING\KSPModControl.xml --game "C:\Users\rjwol\Desktop\Kerbal Space Program"

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
URLs and probes the routes. Private packages are never uploaded; their file names are listed for hand-out.

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
