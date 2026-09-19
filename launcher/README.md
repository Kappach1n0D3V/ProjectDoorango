# ProjectDoorango Launcher

Native Windows x64 launcher. Download [ProjectDoorango.exe](https://github.com/Kappach1n0D3V/ProjectDoorango/releases/latest/download/ProjectDoorango.exe), put it in an empty writable folder and click **Install game**. Files are verified before the button changes to **Play**. Use **Start server (host your world)** to download and start a local world, or enter a remote IP/port and choose **Use server**. Play connects to the selected server without starting a local one.

## Try it

Run `ProjectDoorango.exe` in the repository root. The packaged launcher is self-contained for Windows x64. It finds `game/` and `server/` beside the executable; development builds also find the repository above them.

- **Play:** checks the selected server’s `/knock` readiness response, then starts the game. Installation and local server startup are separate actions. An already-running server can be used without taking ownership of it.
- **Start / Stop:** builds and starts the local server, or requests a save and orderly shutdown. It never force-kills a game server. Closing the launcher also stops a server it started.
- **Downloads:** checks `Kappach1n0D3V/ProjectDoorango/main`, pins an exact commit, and obtains its complete Git file list. The client comes from `game/` in that repository. Each extracted file must match its expected size and Git blob hash before installation. Downloads support progress, cancellation, staged extraction, and a backup of the previous installation. Settings in `offserver.txt`, including `account=`, are preserved.
- **Server download:** installs `server/` from the same repository and verifies every server file against the selected commit. The immutable archive is cached in `.launcher/downloads/` and reused for both installations. Existing server folders are never overwritten.
- **Settings:** gateway address, game folder, player log, backups, and the .NET SDK download page.
- **Activity:** readable server/build/download output and access to the full launcher log.

No separate .NET installation is required for players. Git checkouts use a local source build and require the .NET 9 SDK. The launcher itself does not require a separate .NET installation. Downloads start only when requested. Put the launcher in a writable folder, outside Program Files. Client backups consume disk space and are retained for manual recovery.

Local preferences, logs, downloads and backups live in `.launcher/`, which is excluded from Git.

## Limitations

The server runtime is pinned to v0.1.0 with an embedded SHA-256 checksum. Future server code changes require a new runtime and launcher release.

Client installs use full commit archives, not incremental patches. The UI shows installed size; GitHub may not advertise the compressed download size, in which case the transfer shows bytes received and speed. The launcher has no launcher self-updater. Manually customized client files remain in the backup; only the connection/account configuration is carried into a replacement installation.

## Build

```powershell
dotnet build launcher/ProjectDoorango.Launcher.csproj -c Release
dotnet publish launcher/ProjectDoorango.Launcher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o launcher/publish
```

Copy `launcher/publish/ProjectDoorango.exe` to the installation root.

## Verification

```powershell
launcher/bin/Release/net9.0-windows/ProjectDoorango.exe --self-test launcher-tests.log
launcher/bin/Release/net9.0-windows/ProjectDoorango.exe --server-smoke launcher-server-smoke.log
launcher/bin/Release/net9.0-windows/ProjectDoorango.exe --download-smoke launcher-download-smoke.log
```

The 43 self-checks exercise settings preservation, unsafe/duplicate ZIP paths, extraction cancellation, successful/failed installation swaps, checksums, Git blob verification, the complete download/install path using a local HTTP fixture, shared archive reuse, and installation rollback behavior.

The server smoke check copies source/data into a temporary directory, downloads the published runtime and starts a real server on isolated ports, verifies readiness, requests graceful shutdown, and checks live ProjectDoorango commit metadata. It does not touch existing player saves. The download smoke check fetches the real repository archive, installs the client and server in isolation, verifies every installed repository file, and retains the archive in the normal download cache. Existing gameplay regressions also pass.

## Credits

Built on [ShuuuuShi's Durango work](https://github.com/ShuuuuShi), with the client originally obtained from [Durango-OffServer-Client](https://github.com/ShuuuuShi/Durango-OffServer-Client). Launcher downloads now come exclusively from [ProjectDoorango](https://github.com/Kappach1n0D3V/ProjectDoorango). Original game by NEXON / What! Studio.
