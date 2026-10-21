# Hunter Tracking

An [Eco](https://play.eco) mod that helps you find the animals you are hunting. Add animal species to your tracking list and every matching animal near you gets a **green silhouette** outline that is visible only inside a configurable range.

Compatible with Eco **0.14.2.1** (net10.0).

## Features

- Track up to a configurable number of animal species at the same time.
- Matching animals alive and inside the configured range are highlighted with the game's Master Tracker green silhouette.
- Highlights refresh continuously and disappear automatically when the animal dies, leaves the range, or is removed from the list.
- Per-player configuration, persisted across server restarts.

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
