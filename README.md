# Hunter Tracking

An [Eco](https://play.eco) mod that helps you find the animals you are hunting. Add animal species to your tracking list and every matching animal near you gets a **green silhouette** outline that is visible only inside a configurable range.

Compatible with Eco **0.14.2.1** (net10.0).

## Features

- Track up to a configurable number of animal species at the same time.
- Matching animals alive and inside the configured range are highlighted with the game's green tracking silhouette (`HighlightPurpose.Common` - the talent-free channel).
- Highlights refresh continuously every 0.5 s with an infinite lifetime and disappear automatically when the animal dies, leaves the range, or is removed from the list.
- Per-player configuration, persisted across server restarts.
- Server log diagnostics: highlight failures and per-pass counts are written via `Log.Debug` ("HunterTracking: ...") for troubleshooting.
- `/ht locate` fallback (added in 0.1.4): live bearing (compass direction + degrees), distance and world coordinates of the nearest tracked animal, so you can still hunt without any silhouette.

## Silhouette visibility & through-object rendering

Eco's server ModKit only exposes `User.ConstantHighlightSetting.TryAddOrUpdate(...)` with `HighlightPurpose.MasterTracker` (green tracking outline) or `Common`; it provides **no API to toggle occlusion / x-ray / always-visible rendering** (verified against Eco.ReferenceAssemblies 0.14.2.1 - there are no `Occlusion`, `XRay`, `AlwaysVisible`, `Transparent`, `Outline` or renderer hook types). The green outline itself is drawn by the game client and is not occlusion-gated, so a silhouette that is actually active will be visible through terrain, walls and other objects. If an animal behind an object does **not** show a silhouette, that means the highlight entry is not active - check:

1. You have the species tracked (`/ht list` shows "Currently highlighted" count).
2. The animal is alive and inside your configured range.
3. Your range is not too large for the game's network visibility - silhouettes require the animal to be loaded/synced on your client, so keep the range reasonable (default 50 m works everywhere).
4. The server log contains `HunterTracking: N/M highlight updates failed` lines only rarely; repeated failures usually mean the animal is not synced to your client.

## 0.1.6: revert of the 0.1.5 asset-bundle override

0.1.5 shipped a client asset bundle (`HunterTracking.unity3d`, built with the official Eco ModKit: a `ModkitPrefabContainer` scene with 26 animal prefabs - one per species, `ReuseVanillaPrefab.SourceName = <animal short name>` + `HighlightableObject.seeThrough = true`). On live servers every matching animal became **invisible on the client** (nothing renders even at the animal's exact location; the entity logic keeps running server-side).

**Root cause**: the client picked up those prefabs as the animal render source, but the `ReuseVanillaPrefab` reuse mechanism does not resolve vanilla **animal** render assets - it resolves vanilla WorldObject prefabs by name, and animals are not WorldObjects. The empty override prefab was therefore rendered in place of the vanilla animal: no mesh, nothing visible. `HighlightableObject` was not the cause (it only adds a highlight marker); the whole override approach could not be made safe in the short term, so **v0.1.6 removes the bundle entirely**.

**What v0.1.6 does**: no `.unity3d` is shipped; the server no longer streams a render override, so the client renders vanilla animals normally again (fixed on the next client load). The supported, talent-free highlight fallback is unchanged from 0.1.4: the `HighlightPurpose.Common` silhouette feed + `/ht locate` bearing/distance/coordinates. The ModKit Unity experiment stays in `EcoModKit/` at repo root for future reference but is not part of the release.

## 0.1.4: custom rendering research & `/ht locate` fallback

The goal of 0.1.4 was to render the green silhouette ourselves on the client (through-object visible, no talent, no built-in highlight pipeline). Research against the official ModKit documentation and `Eco.ReferenceAssemblies 0.14.2.1` shows this is **not achievable through official channels**:

| Question | Finding |
| --- | --- |
| Does Eco load the mod DLL on the client too? | No. The client loads only asset bundles (`.unity3d`: meshes, icons, animations) streamed by the server; a mod's own C# DLL is never loaded by the client. |
| Is there a client-side plugin entry point? | No. `IModKitPlugin` / `IInitializablePlugin` / `IShutdownablePlugin` / `IThreadedPlugin` are all server-side; there is no `IModKitClientPlugin`. |
| Can mods call Unity rendering APIs (`Graphics.DrawMesh` / `CommandBuffer` / `OnRenderObject` / `MonoBehaviour`)? | No. The reference assemblies contain no `Client` namespace and no reference to `UnityEngine`, so those APIs are absent from the modding surface. |
| Is BepInEx/Harmony an option? | No supported path: the Eco client ships as a self-contained build. |

**Fallback shipped in 0.1.4**: `/ht locate [species]` reports the nearest live tracked animal with its compass bearing (+ degrees), distance in meters and world coordinates `(x, z)`, computed server-side with `World.WrappedDistance` / `World.ClosestWrappedLocation` / `Animal.Position` / `User.Position` - no talent and no highlight channel involved. Use it to navigate toward your target even when silhouettes cannot render (e.g. client/network limits, no visual feedback).

## Commands

All commands use the `/ht` prefix and require normal user level (no admin rights).

| Command | Description |
| --- | --- |
| `/ht help` | Shows the command overview. |
| `/ht species` | Lists the animal species currently present in the world. |
| `/ht add <animal>` | Starts tracking an animal species, e.g. `/ht add deer`. |
| `/ht remove <animal>` | Stops tracking an animal species. |
| `/ht list` | Shows your tracked species and current settings. |
| `/ht clear` | Clears your tracking list. |
| `/ht range <meters>` | Sets the distance at which silhouettes are visible (default `50`). |
| `/ht limit <count>` | Sets the maximum number of tracked species (default `5`). |
| `/ht highlightlimit <count>` | Sets the maximum number of silhouettes shown at once (default `10`). |
| `/ht toggle <on\|off>` | Enables or disables highlighting (default `on`). |
| `/ht locate [species]` | Shows bearing, distance and coordinates of the nearest tracked animal (fallback when silhouettes are unavailable). |

> Only species that currently exist in the world can be tracked - run `/ht species` first to see what is available.

## Build

```powershell
dotnet build -c Release
```

The compiled mod lives in `bin/Release/net10.0/`. For installation, zip up the `HunterTracking.dll`, `modinfo.json` and the `Locales/` folder, then drop the archive into the Eco `Mods` folder (v0.1.6 ships **no** client asset bundle - see the 0.1.6 revert note above).

## Credits

Original author: **Beinded (JuanBerta)**
