# ProjectDoorango

Community fixes and experiments for the Durango: Wild Lands OffServer setup, based on **[ShuuuuShi's work](https://github.com/ShuuuuShi)**.

## Credits

- **[ShuuuuShi](https://github.com/ShuuuuShi)** — upstream server port, OffServer client distribution, and the foundation this project builds on. See [Durango-CustomServer](https://github.com/ShuuuuShi/Durango-CustomServer) and [Durango-OffServer-Client](https://github.com/ShuuuuShi/Durango-OffServer-Client).
- **NEXON / What! Studio** — the original Durango: Wild Lands game and its assets.
- **Kappach1n0D3V / ProjectDoorango** — this project's fixes, regression checks, and functionality tracking.

This is an independent community project, not an official NEXON release. Upstream code comments and attribution are retained. No new license is asserted over upstream code or game assets.

## Current fixes

- Personal-island maps resolve their terrain template correctly.
- Grazing and recalling pets immediately update player-save state.
- Chasing uses actual run animation clips for 205 animal types, extracted and verified against installed client assets. Wandering retains walking animations.

Eight mammoth-family types still need adult/baby run mapping resolved; one decorative type has no verified run clip. Real-client verification remains pending. Several social and story systems are unfinished; see [FUNCTIONALITY-AUDIT.md](FUNCTIONALITY-AUDIT.md) for fixed, incomplete, and unverified functionality.

## Install on Windows

1. Download [**ProjectDoorango.exe**](https://github.com/Kappach1n0D3V/ProjectDoorango/releases/latest/download/ProjectDoorango.exe) from the [latest release](https://github.com/Kappach1n0D3V/ProjectDoorango/releases/latest).
2. Put it in an empty, writable folder, such as `C:\Games\ProjectDoorango`. Windows x64 is required; no separate .NET installation is needed.
3. Run the launcher and click **Install game**. Allow space for the download, extracted files, and future backups. The client alone is approximately 1 GiB installed.
4. Wait for download and file verification to finish. The button changes to **Play Durango**. Installation does not launch the game or start a server.

You only need the launcher EXE. The server ZIP in the release assets is downloaded automatically when needed; do not extract it manually.

## Play in your own local world

1. Click **Start server (host your world)** beside Play. On first use, it downloads the server files and runtime.
2. Wait for **SERVER ONLINE**, then click **Play Durango**.
3. Create your character in the game. Keep the launcher open while playing.
4. Exit the game before choosing **Save & stop** or closing the launcher. The launcher saves and stops servers it started.

## Join another server

1. In **JOIN A SERVER**, enter the host's IP or hostname and gateway port, for example `192.168.1.20:8190`. A bare IP uses port `8190`; full HTTP/HTTPS addresses also work.
2. Click **Use server**, then **Play Durango**. The selected server must be online and reachable.
3. You do **not** need to click Start server or install local server files to join someone else's server. Play never starts a local server automatically.

Close the game and stop any server owned by this launcher before switching addresses. Ask the host for the gateway address; the game and gateway ports are different.

## Updates, saves, and troubleshooting

- **Launcher update:** close the launcher, download the latest EXE, and replace it in the same folder. Launcher self-update is not yet implemented.
- **Game update:** open **Downloads**, choose **Check for updates**, then **Install / repair**. Existing client files are backed up; connection/account settings are preserved. Downloads currently use full archives.
- **Saved progress:** saves belong to the server you play on. Keep local `AppData*` folders when moving or reinstalling a local server. Saves are excluded from GitHub.
- **Account:** `game/offserver.txt` stores the connection and optional `account=` key. Set the same key before first play on another PC to use the same identity on the same server; this does not transfer a locally hosted world.
- **Cannot connect:** check the address and port, confirm the host is online, and open **Troubleshooting** for launcher/server logs. **Settings → Player log** opens the game log.
- Existing server folders are preserved. The bundled runtime remains version 0.1.0; future server-code updates require a new runtime release.

See [launcher/README.md](launcher/README.md) for launcher build instructions and verification details.

## Build and run from source

Requires the **.NET 9 SDK**. From the repository root:

```powershell
dotnet build server/DurangoServer.csproj -c Release
dotnet run --project server/DurangoServer.csproj -c Release -- --data server/data --terrains server/data/terrains
```

The complete client is included in `game/`. After cloning, copy `game/offserver.example.txt` to `game/offserver.txt` to connect to the local server. Set your own optional `account=` key before creating a character; your personal connection file is excluded from Git. Upstream client provenance: [ShuuuuShi/Durango-OffServer-Client](https://github.com/ShuuuuShi/Durango-OffServer-Client/releases).

`PlayDurango.bat` is the existing Windows launcher; its server commands expect .NET at `C:\Program Files\dotnet\dotnet.exe`.

## Checks

```powershell
dotnet run --project tests/RegressionChecks.csproj -c Release -- server/data
```

The regression suite checks personal-island map responses, grazing save notifications, run selection for all 205 mapped animal types, and walking fallback behavior. It uses isolated test state; it does not establish that every game menu or animation has been verified visually.

## Animation extraction

With the game installed locally and `uv` available:

```powershell
uv run --with UnityPy python tools/extract-animal-runs.py --write
```

This updates the server's animation name table and writes [animal-framework-audit.json](animal-framework-audit.json), documenting verified asset references and unresolved mappings. It does not modify the game bundles.

The client binaries and assets are tracked in Git (approximately 1 GiB). Legacy patch binaries, build output, logs, player/world saves, personal connection settings, and local access lists are excluded. Client files retain their exact bytes through `.gitattributes`.
