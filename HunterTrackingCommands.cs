using Eco.Gameplay.Players;
using Eco.Gameplay.Systems.Messaging.Chat.Commands;
using Eco.Shared.Localization;
using Eco.Shared.Services;
using Eco.Shared.Utils;
using Eco.Shared.Voxel;
using Eco.Simulation.Agents;
using Eco.Simulation.Animals;
using Eco.Simulation.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;

namespace HunterTracking
{
    [ChatCommandHandler]
    public static class HunterTrackingCommands
    {
        [ChatCommand("Hunter Tracking commands: track specific animals and see them highlighted with a green silhouette inside a configurable range.", "ht")]
        public static void HunterTracking() { }

        [ChatSubCommand(nameof(HunterTracking), "help", ChatAuthorizationLevel.User)]
        public static void Help(User user)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Localizer.DoStr("/ht help - this help"));
            sb.AppendLine(Localizer.DoStr("/ht species - list animal species currently present in the world"));
            sb.AppendLine(Localizer.DoStr("/ht add <animal> - start tracking an animal species"));
            sb.AppendLine(Localizer.DoStr("/ht remove <animal> - stop tracking an animal species"));
            sb.AppendLine(Localizer.DoStr("/ht list - show your tracked species and current settings"));
            sb.AppendLine(Localizer.DoStr("/ht clear - stop tracking everything"));
            sb.AppendLine(Localizer.DoStr("/ht range <meters> - set the distance at which silhouettes are visible (default 50)"));
            sb.AppendLine(Localizer.DoStr("/ht limit <count> - set the maximum number of tracked species (default 5)"));
            sb.AppendLine(Localizer.DoStr("/ht highlightlimit <count> - set the maximum number of silhouettes shown at once (default 10)"));
            sb.AppendLine(Localizer.DoStr("/ht toggle <on|off> - enable or disable highlighting"));
            sb.AppendLine(Localizer.DoStr("/ht locate [species] - show distance, bearing and coordinates of the nearest tracked animal (fallback when silhouettes are unavailable)"));
            user.Player.MsgLoc($"{sb.ToStringLoc()}", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "species", ChatAuthorizationLevel.User)]
        public static void Species(User user)
        {
            var species = AnimalsManager.Obj.Animals
                .Select(a => a.Species)
                .Where(s => s != null)
                .GroupBy(s => s.Name)
                .Select(g => g.First())
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (species.Count == 0)
            {
                user.Player.ErrorLoc($"No animals are currently present in the world.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(Localizer.DoStr("Animal species currently present in the world:"));
            foreach (var s in species)
                sb.AppendLine(s.Name);
            user.Player.MsgLoc($"{sb.ToStringLoc()}", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "add", ChatAuthorizationLevel.User)]
        public static void Add(User user, string speciesName)
        {
            var config = GetConfig(user);
            var species = FindSpecies(speciesName);
            if (species == null)
            {
                user.Player.ErrorLoc($"No animal species matching '{speciesName}' is present in the world. Run /ht species to see what is available.");
                return;
            }

            if (config.TrackedSpecies.Any(n => n.Equals(species.Name, StringComparison.OrdinalIgnoreCase)))
            {
                user.Player.ErrorLoc($"You are already tracking {species.Name}.");
                return;
            }

            if (config.TrackedSpecies.Count >= Math.Max(1, config.MaxTrackedSpecies))
            {
                user.Player.ErrorLoc($"Tracked species limit reached ({config.MaxTrackedSpecies}). Remove one first or raise the limit with /ht limit <count>.");
                return;
            }

            config.TrackedSpecies.Add(species.Name);
            user.Player.MsgLoc($"Now tracking {species.Name} ({config.TrackedSpecies.Count}/{config.MaxTrackedSpecies}).", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "remove", ChatAuthorizationLevel.User)]
        public static void Remove(User user, string speciesName)
        {
            var config = GetConfig(user);
            var match = config.TrackedSpecies.FirstOrDefault(n => n.Equals(speciesName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                user.Player.ErrorLoc($"'{speciesName}' is not in your tracked list. Run /ht list to see what you are tracking.");
                return;
            }

            config.TrackedSpecies.Remove(match);
            user.Player.MsgLoc($"Stopped tracking {match} ({config.TrackedSpecies.Count}/{config.MaxTrackedSpecies}).", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "list", ChatAuthorizationLevel.User)]
        public static void List(User user)
        {
            var config = GetConfig(user);
            var sb = new StringBuilder();
            sb.AppendLine(Localizer.DoStr("Hunter Tracking settings:"));
            sb.AppendLineLoc($"Highlighting: {(config.HighlightEnabled ? "ON" : "OFF")}");
            sb.AppendLineLoc($"Visible range: {config.TrackingRange:0} m");
            sb.AppendLineLoc($"Max tracked species: {config.MaxTrackedSpecies}");
            sb.AppendLineLoc($"Max silhouettes at once: {config.MaxHighlightedAnimals}");
            sb.AppendLineLoc($"Currently highlighted: {HunterTrackingPlugin.Obj.GetActiveHighlightCount(user.Id)}");
            if (config.TrackedSpecies.Count == 0)
            {
                sb.AppendLine(Localizer.DoStr("Tracked species: none"));
            }
            else
            {
                sb.AppendLine(Localizer.DoStr("Tracked species:"));
                foreach (var name in config.TrackedSpecies)
                    sb.AppendLine(" - " + name);
            }
            user.Player.MsgLoc($"{sb.ToStringLoc()}", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "clear", ChatAuthorizationLevel.User)]
        public static void Clear(User user)
        {
            var config = GetConfig(user);
            config.TrackedSpecies.Clear();
            user.Player.MsgLoc($"Tracking list cleared.", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "range", ChatAuthorizationLevel.User)]
        public static void Range(User user, float meters)
        {
            if (meters < 1f || meters > 1000f)
            {
                user.Player.ErrorLoc($"Range must be between 1 and 1000 meters.");
                return;
            }
            var config = GetConfig(user);
            config.TrackingRange = meters;
            user.Player.MsgLoc($"Silhouettes will now be visible within {config.TrackingRange:0} m.", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "limit", ChatAuthorizationLevel.User)]
        public static void Limit(User user, int count)
        {
            if (count < 1 || count > 50)
            {
                user.Player.ErrorLoc($"Limit must be between 1 and 50.");
                return;
            }
            var config = GetConfig(user);
            config.MaxTrackedSpecies = count;
            user.Player.MsgLoc($"Max tracked species set to {config.MaxTrackedSpecies}.", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "highlightlimit", ChatAuthorizationLevel.User)]
        public static void HighlightLimit(User user, int count)
        {
            if (count < 1 || count > 100)
            {
                user.Player.ErrorLoc($"Highlight limit must be between 1 and 100.");
                return;
            }
            var config = GetConfig(user);
            config.MaxHighlightedAnimals = count;
            user.Player.MsgLoc($"Max silhouettes shown at once set to {config.MaxHighlightedAnimals}.", NotificationStyle.Chat);
        }

        [ChatSubCommand(nameof(HunterTracking), "toggle", ChatAuthorizationLevel.User)]
        public static void Toggle(User user, bool enabled)
        {
            var config = GetConfig(user);
            config.HighlightEnabled = enabled;
            user.Player.MsgLoc($"Hunter Tracking highlighting is now {(enabled ? "ON" : "OFF")}.", NotificationStyle.Chat);
        }

        // 0.1.4: client-side custom rendering (Graphics.DrawMesh / CommandBuffer /
        // OnRenderObject / custom MonoBehaviour) is not reachable from the public ModKit:
        // Eco loads only server plugins and streams asset bundles to the client, so there is
        // no official way to inject client code. As the fallback, /ht locate gives the
        // player live direction/bearing + distance + world coordinates of the nearest
        // tracked animal using only server-side, talent-free APIs.
        [ChatSubCommand(nameof(HunterTracking), "locate", ChatAuthorizationLevel.User)]
        public static void Locate(User user, string? speciesName = null)
        {
            var config = GetConfig(user);
            var wanted = new HashSet<string>(config.TrackedSpecies, StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(speciesName))
            {
                var species = FindSpecies(speciesName);
                if (species == null)
                {
                    user.Player.ErrorLoc($"No animal species matching '{speciesName}' is present in the world. Run /ht species to see what is available.");
                    return;
                }
                wanted = new HashSet<string> { species.Name };
            }

            if (wanted.Count == 0)
            {
                user.Player.ErrorLoc($"You are not tracking any species. Add one with /ht add <animal> first.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(Localizer.DoStr("Nearest tracked animals:"));
            foreach (var name in wanted)
            {
                var animal = AnimalsManager.Obj.Animals
                    .Where(a => a != null && a.Active && !a.Dead && a.Species != null && a.Species.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(a => World.WrappedDistance(a.Position, user.Position))
                    .FirstOrDefault();

                if (animal == null)
                {
                    sb.AppendLineLoc($" - {name}: no live animal found");
                    continue;
                }

                var distance = World.WrappedDistance(animal.Position, user.Position);
                var closest = World.ClosestWrappedLocation(user.Position, animal.Position);
                var dx = closest.X - user.Position.X;
                var dz = closest.Z - user.Position.Z;
                var bearingDeg = (float)(Math.Atan2(dx, dz) * 180.0 / Math.PI);
                if (bearingDeg < 0f) bearingDeg += 360f;
                var compass = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
                var compassIdx = (int)Math.Round(bearingDeg / 45f) % 8;

                sb.AppendLineLoc($" - {name}: {distance:0} m {compass[compassIdx]} ({bearingDeg:0} deg), at ({animal.Position.X:0}, {animal.Position.Z:0})");
            }
            sb.AppendLineLoc($"Your position: ({user.Position.X:0}, {user.Position.Z:0}).");
            user.Player.MsgLoc($"{sb.ToStringLoc()}", NotificationStyle.Chat);
        }

        private static UserConfig GetConfig(User user)
        {
            if (!HunterTrackingData.Obj.UserConfiguration.TryGetValue(user.Id, out var config))
            {
                config = new UserConfig();
                HunterTrackingData.Obj.UserConfiguration[user.Id] = config;
            }
            return config;
        }

        private static AnimalSpecies? FindSpecies(string input)
        {
            var wanted = input.Trim();
            if (wanted.Length == 0)
                return null;

            return AnimalsManager.Obj.Animals
                .Select(a => a.Species)
                .Where(s => s != null)
                .GroupBy(s => s.Name)
                .Select(g => g.First())
                .FirstOrDefault(s =>
                    s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                    wanted.Contains(s.Name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
