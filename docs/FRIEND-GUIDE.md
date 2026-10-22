# Joining VMP Train Career

You need: your own **Kerbal Space Program 1.12.5** (Steam build 03190) with **Making History** and
**Breaking Ground**, about **15 GB** free disk space, and the **server password** from the host.

## 1. Install the launcher

Download `VmpLauncher-win-Setup.exe` from the latest release and run it.

- SmartScreen may say "Windows protected your PC". Click **More info**, then **Run anyway** (the EXE is not
  code-signed; certificates cost money, the launcher is free).
- If you do not have the .NET 10 Desktop Runtime, the installer fetches it (one UAC prompt).

## 2. First run

1. **Find KSP**: the launcher lists Steam installs it found. Pick yours or browse to the folder with `KSP_x64.exe`.
2. **Profile folder**: where the modded copy lives. Same drive as your KSP = less disk use. Keep the path short.
3. **Player**: your in-game name (1–15 letters/digits/`_`/`-`, different from the host's) and the server password.
   The password is saved only on your PC. Accept the VMP disclaimer.

## 3. Install

Press **Install** on the Home page. The launcher downloads ~8.7 GB of mods from their upstream sources and verifies
every file. If the pack needs a **private package** (ApexGuidance), the host sends you a zip: drop it onto the Home
page and it is checked against the pack before anything is installed. You can cancel at any time; nothing is left
half-installed.

## 4. Play

On the **Play** page pick a server route. **Playit** works from anywhere (no port forwarding, no extra software).
**ZeroTier** needs the ZeroTier client installed and the host to authorize you. Press **Play**: KSP starts from the
modded copy and joins the server automatically.

First load with this many mods takes several minutes. That is normal.

## Something wrong?

- **Connection Doctor** runs the checks and tells you what to do in plain language. **Copy report** gives you text to
  paste to the host (it never contains your password).
- **Home → Repair** fixes damaged or missing files.
- **Home → Update** appears when the host publishes a new pack version; only changed mods are downloaded.
- Your saves live in the profile's `game\saves` folder and are never touched by installs or updates.
