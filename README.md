# Hunter Tracking

An [Eco](https://play.eco) mod that helps you find the animals you are hunting. Add animal species to your tracking list and every matching animal near you gets a **green silhouette** outline that is visible only inside a configurable range.

Compatible with Eco **0.14.2.1** (net10.0).

## Features

- Track up to a configurable number of animal species at the same time.
- Matching animals alive and inside the configured range are highlighted with the game's Master Tracker green silhouette.
- The silhouette is **not blocked by terrain or buildings** - the green outline stays visible through objects as long as the highlight is active (Eco renders `MasterTracker` highlights on the client without occlusion gating; the public ModKit exposes no x-ray toggle, see "Silhouette visibility" below).
- Highlights refresh continuously every 0.5 s with a 5 s lifetime and disappear automatically when the animal dies, leaves the range, or is removed from the list.
- Per-player configuration, persisted across server restarts.
- Server log diagnostics: highlight failures and per-pass counts are written via `Log.Debug` ("HunterTracking: ...") for troubleshooting.

## Silhouette visibility & through-object rendering

Eco's server ModKit only exposes `User.ConstantHighlightSetting.TryAddOrUpdate(...)` with `HighlightPurpose.MasterTracker` (green tracking outline) or `Common`; it provides **no API to toggle occlusion / x-ray / always-visible rendering** (verified against Eco.ReferenceAssemblies 0.14.2.1 - there are no `Occlusion`, `XRay`, `AlwaysVisible`, `Transparent`, `Outline` or renderer hook types). The green outline itself is drawn by the game client and is not occlusion-gated, so a silhouette that is actually active will be visible through terrain, walls and other objects. If an animal behind an object does **not** show a silhouette, that means the highlight entry is not active - check:

1. You have the species tracked (`/ht list` shows "Currently highlighted" count).
2. The animal is alive and inside your configured range.
3. Your range is not too large for the game's network visibility - silhouettes require the animal to be loaded/synced on your client, so keep the range reasonable (default 50 m works everywhere).
4. The server log contains `HunterTracking: N/M highlight updates failed` lines only rarely; repeated failures usually mean the animal is not synced to your client.

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

> Only species that currently exist in the world can be tracked - run `/ht species` first to see what is available.

## Build

```powershell
dotnet build -c Release
```

The compiled mod lives in `bin/Release/net10.0/`. For installation, zip up the `HunterTracking.dll`, `modinfo.json` and `Locales/` folder and drop the archive into the Eco `Mods` folder.

## Credits

Original author: **Beinded (JuanBerta)**
