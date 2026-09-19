# OffServer functionality audit — 18 September 2026

The local server is ahead of several entries in the supplied 14 September client README. A registered handler does not necessarily implement a feature: several return `Abort` or empty results.

## Fixed in this pass

- **Personal-island maps:** `GetRegionMapInfo` treated a logical `personal_*` region ID as a terrain filename. It now resolves the terrain template, preserves the requested region ID, and uses the current logical region to choose current-world fog data. A different island using the same terrain does not inherit current-world fog.
- **Grazing save state:** changing the grazing selection saved the world but omitted `OnContextChanged()`, leaving player-save state stale until a later save-triggering action. Both grazing and recall now notify the existing player persistence path.
- **Animal chase animations:** extracted real run clips from the installed Unity asset bundles and added run mappings for 205 of 214 animal types. Chasing now selects the run clip; wandering retains the walk clip. Chase speed, path timing, and attack distance are unchanged. Older tables fall back to walking.

## Verification

Release build succeeds with three existing warnings. Checks passed:

| Check | Assertions |
| --- | ---: |
| Map, grazing and animal-animation regressions | 18, plus chase validation for all 205 mapped types |
| Farming | 42 |
| Quest catalog | 126 |
| Level-up effects | 67 |
| World status effects | 45 |

Run the regression suite from this directory:

```powershell
& 'C:/Program Files/dotnet/dotnet.exe' run --project tests/RegressionChecks.csproj -c Release -- server/data
```

The regression harness calls real handlers with isolated in-memory world/player state and checks map packets over loopback TCP. It checks grazing synchronization into the player-save context, not a full restart from disk. It does not boot or overwrite a player's world.

Real game menus, island rendering, reconnect persistence, and multiplayer sessions have **not** been verified. Restart the local server to load the rebuilt assembly before testing.

## Remaining findings

| Area | Local source finding | Remaining work |
| --- | --- | --- |
| Estates and estate visits | Implementations exist in `Player.PersonalRegion.cs` | End-to-end client checks, including permissions and travel |
| Pet grazing | Implementation exists; save notification fixed | Client interaction and restart checks |
| Tutorial | Four `TutorialEvent` actions implemented | `GuideProgress` has no handler; verify client semantics before adding persistence |
| Story progression | `RequestEpicWarp` and epic NPC interaction explicitly abort | Story quest engine and chapter destination data |
| Friends / following / blocking | Mutation handlers explicitly abort | Persistent relationships and client notifications |
| Parties | Mutation handlers explicitly abort | Membership, invitations, leadership and disconnect behavior |
| Clans | Handler presence alone does not establish functionality | Detailed state and protocol audit |
| Player market listings | Registration aborts; history queries return empty results | Listings, purchases, settlement and persistence |
| Shared music | Publishing and shared playback are unavailable | Shared storage and playback protocol |
| Map landmarks | World construction clears `global_landmarks` | Verify client expectations and terrain landmark data |
| Animal run animations | 205 types use verified run clips; 8 mammoth-family types have ambiguous adult/baby mappings, and `Watermelon_Stand` has no verified run mapping | Resolve those variants; verify animations in the running game |
| Launcher updates | Client binaries present; no client/launcher source project in this folder | Obtain matching source or inspect binaries before changing behavior |

This is an initial audit, not a claim that every remaining feature has been identified or repaired.

Animation extraction is reproducible with `uv run --with UnityPy python tools/extract-animal-runs.py --write`. The extractor verifies each selected framework pointer references an actual `AnimationClip` with the exact expected name. It records bundle names, movement sets, mappings and unresolved entries in `animal-framework-audit.json`. It does not edit client bundles. Visual playback in the game remains untested.

Upstream references: [server](https://github.com/ShuuuuShi/Durango-CustomServer), [client releases](https://github.com/ShuuuuShi/Durango-OffServer-Client).
