using Eco.Core.Plugins;
using Eco.Core.Plugins.Interfaces;
using Eco.Gameplay.Players;
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
using System.Threading.Tasks;

namespace HunterTracking
{
    /// <summary>
    /// Hunter Tracking main plugin. Every server tick (throttled) it walks all online users,
    /// reads their tracked species list and re-issues the "Master Tracker" highlight on the
    /// matching animals that are alive, active and inside the configured range. Animals that
    /// no longer match are un-highlighted on the next pass.
    ///
    /// Rendering note: Eco's public ModKit exposes no "x-ray / always visible" API for
    /// highlights (verified against Eco.ReferenceAssemblies 0.14.2.1: HighlightPurpose has
    /// only Common/MasterTracker and there is no occlusion, transparency or renderer hook).
    /// The green silhouette itself is rendered by the game client for
    /// HighlightPurpose.MasterTracker and is not occlusion-gated: it stays visible through
    /// terrain and buildings as long as the highlight entry is active. The plugin's job is
    /// therefore to keep the entry alive (re-issue every pass), extend the lifetime so no
    /// tick jitter expires it, and report failures so missing silhouettes can be diagnosed.
    /// </summary>
    internal class HunterTrackingPlugin : Singleton<HunterTrackingPlugin>, IModKitPlugin, ISaveablePlugin, IThreadedPlugin
    {
        [NotNull] private readonly HunterTrackingData data;
        private DateTime lastHighlightPass = DateTime.MinValue;
        private const double HighlightPassIntervalSeconds = 0.5;
        // Longer lifetime than the pass interval so a single missed/failed pass can never
        // let a silhouette expire and disappear before the next refresh.
        private const double HighlightDurationSeconds = 5.0;

        // Per-user set of animals currently highlighted, so highlights can be removed when an
        // animal leaves the range, dies or is removed from the tracked list.
        private readonly Dictionary<int, HashSet<Animal>> activeHighlights = new();
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
                    continue;

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
                    if (!setting.TryAddOrUpdate(candidate.Animal, HighlightPurpose.MasterTracker, HighlightDurationSeconds))
                        failures++;
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
                            setting.TryRemove(animal, HighlightPurpose.MasterTracker);
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

        private void ClearHighlights(User user)
        {
            if (user.ConstantHighlightSetting == null)
                return;
            HashSet<Animal>? previous;
            lock (highlightsLock)
            {
                activeHighlights.TryGetValue(user.Id, out previous);
                if (previous != null)
                    activeHighlights.Remove(user.Id);
            }
            if (previous == null)
                return;
            foreach (var animal in previous)
                user.ConstantHighlightSetting.TryRemove(animal, HighlightPurpose.MasterTracker);
        }

        /// <summary>Number of silhouettes currently maintained for a user (diagnostic for /ht list).</summary>
        public int GetActiveHighlightCount(int userId)
        {
            lock (highlightsLock)
            {
                return activeHighlights.TryGetValue(userId, out var set) ? set.Count : 0;
            }
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
