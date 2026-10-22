# Hunter Tracking

An [Eco](https://play.eco) mod that helps you find the animals you are hunting. Add animal species to your tracking list and every matching animal near you gets a **personal neon-green marker on the M-key map and minimap** (`WorldMarker` layer, updated live) inside a configurable range.

Compatible with Eco **0.14.2.1** (net10.0).

## Features

- Track up to a configurable number of animal species at the same time.
- Matching animals alive and inside the configured range are shown as **neon-green personal map markers** (`WorldMarker`, one marker per animal) on the in-game **M-key map** and minimap that follow the animal live (repositioned every 0.5 s pass).
- Markers are **personal** to each player (`WorldMarkerUtils.MakeMarkerPersonal`), so nobody else sees them.
- Markers refresh continuously every 0.5 s and disappear automatically when the animal dies, leaves the range, or is removed from the tracked list, or when tracking is toggled off.
- Per-player configuration, persisted across server restarts.
- Server log diagnostics: marker creation/removal failures and per-pass counts are written via `Log.Debug` ("HunterTracking: ...") for troubleshooting.
- `/ht locate` fallback (added in 0.1.4): live bearing (compass direction + degrees), distance and world coordinates of the nearest tracked animal, so you can still hunt without any marker.

## 0.1.8: what was broken and what changed

**0.1.7 failure**: on a live client neither the green silhouette (`HighlightPurpose.Common`) nor the map markers were visible; only `/ht locate` worked.

**Root causes found**:

1. **Silhouette channel is impossible server-side (confirmed, removed in 0.1.8).** `ConstantHighlightSetting.TryAddOrUpdate(HighlightPurpose.Common)` is the only server-side highlight feed, but the client renders that outline only for entities it has synced *and* only through the in-game MasterTracker talent flow. The public ModKit cannot inject client rendering code (see 0.1.4 research), so a talent-free `Common` feed never produces a visible silhouette. 0.1.8 removes the silhouette channel entirely.

2. **Markers were created without `SetVisiblity`.** `WorldMarkerVisibility` is a `[Flags]` enum whose flags are registered at creation time; without an explicit call the client did not register the marker for rendering on live servers. **0.1.8 calls `marker.SetVisiblity(WorldMarkerVisibility.ShowInTheWorldAndMinimap)` immediately after every marker creation.** The rest of the marker setup (personal scope, color, label, `SetPos` follow, `SetMaxDistanceToShow` range cap, removal semantics) is unchanged from 0.1.7.

**Asset-bundle research (Unity CLI, official Eco ModKit)**:

- `WorldMarker.IconNameOverride` is **get-only**: a server mod cannot assign a custom icon name. The client looks marker icons up in its own built-in icon library (`StreamingAssets/aa/StandaloneWindows64/icons_assets_all_*.bundle`, ~23 MB) keyed by marker type, so **map markers do not need a custom icon bundle**.
- A real-world floating marker (`MinimapComponent` / `MinimapObject`) is **not attachable to animals**: `MinimapComponent` is a `WorldObjectComponent` and animals are not WorldObjects (same root cause as 0.1.5). The only server-driven marker channel that works for animals is the **map layer** (`WorldMarker` on the M-key map + minimap).
- The optional icon asset bundle `HunterTrackingIcons.unity3d` built with the Unity CLI is an **offline asset-pipeline verification artifact only**: it contains sprites, **never prefabs**, and the mod does not reference it at runtime. It exists to prove the CLI pipeline works and to make the shipped archive self-contained; it does **not** change marker icons.
- **Critical safety rule (kept from 0.1.5/0.1.6)**: the mod must NEVER ship an animal prefab override again. 0.1.5 proved `ReuseVanillaPrefab` cannot resolve vanilla animal render assets, which made every matched animal invisible on the client. 0.1.8 contains no prefab bundle.

## 0.1.7: M-key map markers (history)

The green silhouette is drawn by the game client only for animals that are synced to your client, so an animal behind terrain/buildings or outside the client's loaded area can show no feedback. 0.1.7 added a **map layer** that is not affected by line-of-sight: for every tracked animal inside range, the mod creates a **personal green `WorldMarker`** that appears on the in-game **M-key map**. This reuses the `WorldMarker` implementation first shipped in 0.1.2 and reverted in 0.1.3 (back then the requirement was a real-world indicator; `WorldMarker` renders on the M-key map, not in the 3D world - which is exactly the target). Fixed vs 0.1.2: `SetDisplayOffScreen` is no longer used (its off-screen arrow indicator only made sense for a real-world layer).

## 0.1.6: revert of the 0.1.5 asset-bundle override (history)

0.1.5 shipped a client asset bundle (`HunterTracking.unity3d`, built with the official Eco ModKit: a `ModkitPrefabContainer` scene with 26 animal prefabs - one per species, `ReuseVanillaPrefab.SourceName = <animal short name>` + `HighlightableObject.seeThrough = true`). On live servers every matching animal became **invisible on the client** (nothing renders even at the animal's exact location; the entity logic keeps running server-side).

**Root cause**: the client picked up those prefabs as the animal render source, but the `ReuseVanillaPrefab` reuse mechanism does not resolve vanilla **animal** render assets - it resolves vanilla WorldObject prefabs by name, and animals are not WorldObjects. The empty override prefab was therefore rendered in place of the vanilla animal: no mesh, nothing visible. The whole override approach could not be made safe in the short term, so **v0.1.6 removes the bundle entirely** and the client renders vanilla animals normally again.

## 0.1.4: custom rendering research & `/ht locate` fallback (history)

The goal of 0.1.4 was to render the green silhouette ourselves on the client (through-object visible, no talent, no built-in highlight pipeline). Research against the official ModKit documentation and `Eco.ReferenceAssemblies 0.14.2.1` shows this is **not achievable through official channels**:

| Question | Finding |
| --- | --- |
| Does Eco load the mod DLL on the client too? | No. The client loads only asset bundles (`.unity3d`: meshes, icons, animations) streamed by the server; a mod's own C# DLL is never loaded by the client. |
| Is there a client-side plugin entry point? | No. `IModKitPlugin` / `IInitializablePlugin` / `IShutdownablePlugin` / `IThreadedPlugin` are all server-side; there is no `IModKitClientPlugin`. |
| Can mods call Unity rendering APIs (`Graphics.DrawMesh` / `CommandBuffer` / `OnRenderObject` / `MonoBehaviour`)? | No. The reference assemblies contain no `Client` namespace and no reference to `UnityEngine`, so those APIs are absent from the modding surface. |
| Is BepInEx/Harmony an option? | No supported path: the Eco client ships as a self-contained build. |

**Fallback shipped in 0.1.4**: `/ht locate [species]` reports the nearest live tracked animal with its compass bearing (+ degrees), distance in meters and world coordinates `(x, z)`, computed server-side with `World.WrappedDistance` / `World.ClosestWrappedLocation` / `Animal.Position` / `User.Position` - no talent and no highlight channel involved. Use it to navigate toward your target even when markers cannot render (e.g. client/network limits, no visual feedback).

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
| `/ht range <meters>` | Sets the distance at which map markers are visible (default `50`). |
| `/ht limit <count>` | Sets the maximum number of tracked species (default `5`). |
| `/ht highlightlimit <count>` | Sets the maximum number of map markers shown at once (default `10`). |
| `/ht toggle <on\|off>` | Enables or disables map markers (default `on`). |
| `/ht locate [species]` | Shows bearing, distance and coordinates of the nearest tracked animal (fallback when markers are unavailable). |

> Only species that currently exist in the world can be tracked - run `/ht species` first to see what is available.

## Build

```powershell
dotnet build -c Release
```

The compiled mod lives in `bin/Release/net10.0/`. For installation, zip up the `HunterTracking.dll`, `modinfo.json`, the `Locales/` folder and (optionally, as a self-contained verification artifact) `HunterTrackingIcons.unity3d`, then drop the archive into the Eco `Mods` folder.

## Credits

Original author: **Beinded (JuanBerta)**
