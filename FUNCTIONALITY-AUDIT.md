# OffServer functionality audit — 18 September 2026

The local server is ahead of several entries in the supplied 14 September client README. A registered handler does not necessarily implement a feature: several return `Abort` or empty results.

## Fixed in this pass

### Original-game restoration findings — 20 September 2026 (source fixes; runtime release pending)

- **English gathering text:** extracted the client's `en_US` catalog (33,066 entries) and made the server select English rather than Thai. Regression examples: Leaf, Stalk, Reed. Hard-coded Thai server messages and previously saved translated item names require a separate audit; this is not a claim that every string is English.
- **Empty Tamed Island picker:** client constants listed 13 templates, but the EstateGroup picker has only five preview slots. Its `Awake()` indexes those slots without bounds checking. Client and server choices now use `pe10gr_1` through `pe10gr_5`. Re-reading the modified Unity file confirmed all other 44,375 objects were byte-identical. In-game selection/travel still needs visual validation. Reproduce with `uv run --with UnityPy python tools/fix-client-localization.py`.
- **Level-60 sandbox start:** new `PlayerContext` instances now start at level 1; initializing existing characters preserves their levels. This does not restore the original tutorial by itself.
- **Wildlife diagnosis:** all 13 available wild-island terrain/template pairs produce animals in the server audit; `sn20snow` produces 18. Its appearance packets reach the client protocol and are resent after leaving/re-entering visibility. The five personal templates define zero wild herds. No evidence yet establishes why the reported client shows none; client rendering and the exact installed server build remain to be checked.
- **Original opening remains incomplete:** the client contains the train prologue and a skip/proceed choice, but tutorial-island departure, story warps, and guide progression are not fully implemented by this server. Faithful restoration requires an actual new-character walkthrough through the prologue, tutorial island, departure, and personal-island acquisition. Merely enabling a scene or lowering the level is not completion.

The published launcher's pinned server runtime is still v0.1.0. These server changes need a new runtime package and launcher release before fresh downloads receive them.

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

### Launcher release — 19 September 2026

A local Windows launcher now covers the batch-file controls plus client/server downloads from ProjectDoorango, per-file verification against GitHub Git blob hashes, shared archive caching, backups, cancellation, settings preservation and an activity log. Graceful server shutdown runs through the main loop after a launcher `stop` command. The launcher checks the compiled server's control protocol before taking ownership of it. The release includes a standalone launcher and checksum-verified server runtime; Install & Play downloads missing game and server files. See `launcher/README.md` for verification and remaining download/release limitations.

The game client was uploaded to ProjectDoorango in commit `6b59af6`: all 4,460 remote game-file hashes match the local commit. Personal connection settings and logs remain excluded.

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
| Launcher updates | Native launcher source included; full client downloads and standalone server runtime supported | Incremental patches and launcher self-update remain unimplemented |

This is an initial audit, not a claim that every remaining feature has been identified or repaired.

Animation extraction is reproducible with `uv run --with UnityPy python tools/extract-animal-runs.py --write`. The extractor verifies each selected framework pointer references an actual `AnimationClip` with the exact expected name. It records bundle names, movement sets, mappings and unresolved entries in `animal-framework-audit.json`. It does not edit client bundles. Visual playback in the game remains untested.

Upstream references: [server](https://github.com/ShuuuuShi/Durango-CustomServer), [client releases](https://github.com/ShuuuuShi/Durango-OffServer-Client).
