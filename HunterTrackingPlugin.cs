using Eco.Core.Plugins;
using Eco.Core.Plugins.Interfaces;
using Eco.Gameplay.Players;
using Eco.Gameplay.UI.WorldMarker;
using Eco.Simulation.Agents;
using Eco.Simulation.Animals;
using Eco.Shared.Localization;
using Eco.Shared.Logging;
using Eco.Shared.Math;
using Eco.Shared.Utils;
using Eco.Shared.Voxel;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace HunterTracking
{
    /// <summary>
    /// Hunter Tracking main plugin. Every server tick (throttled) it walks all online users,
    /// reads their tracked species list and maintains a talent-free, always-visible marker on
    /// the matching animals that are alive, active and inside the configured range. Animals
    /// that no longer match are un-marked on the next pass.
    ///
    /// Rendering note (verified against Eco.ReferenceAssemblies 0.14.2.1 + official ModKit
    /// docs): ConstantHighlightSetting is the only server-side highlight channel; it is a
    /// generic per-user list of "objects to keep highlighted on the client" and is NOT gated
    /// behind any talent. HighlightPurpose.MasterTracker is only fed by the in-game
    /// MasterTracker talent, which is why a player without that skill never received any
    /// highlight entry and saw no silhouette. The fix therefore uses HighlightPurpose.Common
    /// (the enum value explicitly documented as the generic, non-talent purpose) with
    /// timeToHighlight &lt;= 0 (infinite): the client renders the silhouette immediately for
    /// every user and keeps it until TryRemove is called. As a second talent-free visual
    /// layer, the plugin also maintains a personal green WorldMarker on each tracked animal
    /// (SetDisplayOffScreen makes it visible through terrain/buildings and off-screen), so
    /// players always have a green, occlusion-independent indicator even where the client's
    /// generic highlight color differs from the MasterTracker green.
    /// </summary>
    internal class HunterTrackingPlugin : Singleton<HunterTrackingPlugin>, IModKitPlugin, ISaveablePlugin, IThreadedPlugin
    {
        [NotNull] private readonly HunterTrackingData data;
        private DateTime lastHighlightPass = DateTime.MinValue;
        private const double HighlightPassIntervalSeconds = 0.5;
        // Infinite highlight: timeToHighlight <= 0 keeps the entry alive until explicitly
        // removed (ClearExpired never removes those), so a missed pass can never expire a
        // silhouette.
        private const double InfiniteHighlightDuration = 0.0;

        // Per-user set of animals currently highlighted (Common purpose), so highlights and
        // world markers can be removed when an animal leaves the range, dies or is removed
        // from the tracked list.
        private readonly Dictionary<int, HashSet<Animal>> activeHighlights = new();
        private readonly Dictionary<int, Dictionary<int, WorldMarker>> activeMarkers = new();
        private readonly object highlightsLock = new();

        public HunterTrackingPlugin()
        {
            data = StorageManager.LoadOrCreate<HunterTrackingData>("HunterTracking");
        }

        public string GetCategory() => Localizer.DoStr("Mods");
        public string GetStatus() => string.Empty;

        public void SaveAll()
        {
            StorageManager.Obj.MarkDirty(data);
        }

        public void Run()
        {
            var now = DateTime.UtcNow;
            if ((now - lastHighlightPass).TotalSeconds < HighlightPassIntervalSeconds)
                return;
            lastHighlightPass = now;
            try
            {
                HighlightPass();
            }
            catch (Exception ex)
            {
                Log.WriteErrorLineLoc($"HunterTracking highlight pass failed: {ex.Message}");
            }
        }

        private void HighlightPass()
        {
            var totalHighlighted = 0;
            foreach (var user in UserManager.Users)
            {
                if (user == null || !user.IsActive)
                    continue;

                if (!data.UserConfiguration.TryGetValue(user.Id, out var config) || !config.HighlightEnabled)
                {
                    ClearHighlights(user);
                    continue;
                }

                if (user.ConstantHighlightSetting == null)
                    user.ConstantHighlightSetting = new ConstantHighlightSetting(user);

                var setting = user.ConstantHighlightSetting;
                var wanted = new HashSet<string>(config.TrackedSpecies, StringComparer.OrdinalIgnoreCase);
                if (wanted.Count == 0)
                {
                    ClearHighlights(user);
                    continue;
                }

                var range = Math.Max(1f, config.TrackingRange);
                var maxCount = Math.Max(1, config.MaxHighlightedAnimals);

                var candidates = AnimalsManager.Obj.Animals
                    .Where(a => a != null && a.Active && !a.Dead && a.Species != null && wanted.Contains(a.Species.Name))
                    .Select(a => new { Animal = a, Distance = World.WrappedDistance(a.Position, user.Position) })
                    .Where(x => x.Distance <= range)
                    .OrderBy(x => x.Distance)
                    .Take(maxCount)
                    .ToList();

                var toHighlight = candidates.Select(x => x.Animal).ToHashSet();

                var failures = 0;
                foreach (var candidate in candidates)
                {
                    if (!setting.TryAddOrUpdate(candidate.Animal, HighlightPurpose.Common, InfiniteHighlightDuration))
                        failures++;
                    UpdateMarker(user, candidate.Animal, range);
                }

                HashSet<Animal>? previous;
                lock (highlightsLock)
                {
                    activeHighlights.TryGetValue(user.Id, out previous);
                }
                if (previous != null)
                {
                    foreach (var animal in previous)
                    {
                        if (!toHighlight.Contains(animal))
                        {
                            setting.TryRemove(animal, HighlightPurpose.Common);
                            RemoveMarker(user, animal);
                        }
                    }
                }
                lock (highlightsLock)
                {
                    activeHighlights[user.Id] = toHighlight;
                }

                totalHighlighted += toHighlight.Count;
                if (failures > 0)
                    Log.Debug($"HunterTracking: {failures}/{candidates.Count} highlight updates failed for {user.Name}, will retry next pass.");
            }

            if (totalHighlighted > 0)
                Log.Debug($"HunterTracking: pass complete, {totalHighlighted} animal(s) highlighted.");
        }

        private void UpdateMarker(User user, Animal animal, float range)
        {
            var player = user.Player;
            if (player == null || user.Markers == null)
                return;

            var speciesName = animal.Species?.Name ?? "Animal";
            var pos = ToVector3i(animal.GroundPosition);

            lock (highlightsLock)
            {
                if (!activeMarkers.TryGetValue(user.Id, out var perUser))
                {
                    perUser = new Dictionary<int, WorldMarker>();
                    activeMarkers[user.Id] = perUser;
                }

                if (perUser.TryGetValue(animal.ID, out var existing))
                {
                    try
                    {
                        existing.SetPos(player, pos);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"HunterTracking: marker reposition failed for {speciesName}: {ex.Message}");
                    }
                    return;
                }

                WorldMarker? marker = null;
                try
                {
                    marker = user.Markers.Add(user, pos, speciesName, false, false, MarkerFolderName.None);
                    if (marker == null)
                        return;
                    marker.SetColor(player, Color.NeonGreen);
                    marker.SetDisplayOffScreen(player, true);
                    marker.SetDisplayDistance(player, false);
                    marker.SetDisplayText(player, true);
                    marker.SetDisplayName(player, speciesName);
                    marker.SetMaxDistanceToShow(player, range);
                    WorldMarkerUtils.MakeMarkerPersonal(user, marker);
                    perUser[animal.ID] = marker;
                }
                catch (Exception ex)
                {
                    Log.Debug($"HunterTracking: marker creation failed for {speciesName}: {ex.Message}");
                    if (marker != null)
                    {
                        try { marker.Remove(player); } catch { }
                    }
                }
            }
        }

        private void RemoveMarker(User user, Animal animal)
        {
            var player = user.Player;
            if (player == null || user.Markers == null)
                return;

            WorldMarker? marker;
            lock (highlightsLock)
            {
                if (!activeMarkers.TryGetValue(user.Id, out var perUser))
                    return;
                if (!perUser.TryGetValue(animal.ID, out marker))
                    return;
                perUser.Remove(animal.ID);
                if (perUser.Count == 0)
                    activeMarkers.Remove(user.Id);
            }

            try
            {
                marker.Remove(player);
            }
            catch (Exception ex)
            {
                Log.Debug($"HunterTracking: marker removal failed: {ex.Message}");
            }
        }

        private void ClearHighlights(User user)
        {
            HashSet<Animal>? previous;
            Dictionary<int, WorldMarker>? markers;
            lock (highlightsLock)
            {
                activeHighlights.TryGetValue(user.Id, out previous);
                if (previous != null)
                    activeHighlights.Remove(user.Id);
                activeMarkers.TryGetValue(user.Id, out markers);
                if (markers != null)
                    activeMarkers.Remove(user.Id);
            }

            if (previous != null && user.ConstantHighlightSetting != null)
            {
                foreach (var animal in previous)
                    user.ConstantHighlightSetting.TryRemove(animal, HighlightPurpose.Common);
            }

            if (markers != null && user.Player != null && user.Markers != null)
            {
                foreach (var marker in markers.Values)
                {
                    try { marker.Remove(user.Player); } catch { }
                }
            }
        }

        /// <summary>Number of silhouettes currently maintained for a user (diagnostic for /ht list).</summary>
        public int GetActiveHighlightCount(int userId)
        {
            lock (highlightsLock)
            {
                return activeHighlights.TryGetValue(userId, out var set) ? set.Count : 0;
            }
        }

        private static Vector3i ToVector3i(Vector3 v)
        {
            return new Vector3i((int)Math.Floor(v.X), (int)Math.Floor(v.Y), (int)Math.Floor(v.Z));
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
