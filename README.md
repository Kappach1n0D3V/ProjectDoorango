# ProjectDoorango

Community fixes and experiments for the Durango: Wild Lands OffServer setup, based on **[ShuuuuShi's work](https://github.com/ShuuuuShi)**.

## Credits

- **[ShuuuuShi](https://github.com/ShuuuuShi)** — upstream server port, OffServer client distribution, and the foundation this project builds on. See [Durango-CustomServer](https://github.com/ShuuuuShi/Durango-CustomServer) and [Durango-OffServer-Client](https://github.com/ShuuuuShi/Durango-OffServer-Client).
- **NEXON / What! Studio** — the original Durango: Wild Lands game and its assets.
- **Sksandeep144 / ProjectDoorango** — this project's fixes, regression checks, and functionality tracking.

This is an independent community project, not an official NEXON release. Upstream code comments and attribution are retained. No new license is asserted over upstream code or game assets.

## Current fixes

- Personal-island maps resolve their terrain template correctly.
- Grazing and recalling pets immediately update player-save state.
- Chasing uses actual run animation clips for 205 animal types, extracted and verified against installed client assets. Wandering retains walking animations.

Eight mammoth-family types still need adult/baby run mapping resolved; one decorative type has no verified run clip. Real-client verification remains pending. Several social and story systems are unfinished; see [FUNCTIONALITY-AUDIT.md](FUNCTIONALITY-AUDIT.md) for fixed, incomplete, and unverified functionality.

## Build and run

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
