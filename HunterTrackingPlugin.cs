using Eco.Core.Plugins;
using Eco.Core.Plugins.Interfaces;
using Eco.Gameplay.Players;
using Eco.Simulation.Agents;
using Eco.Simulation.Animals;
using Eco.Shared.Localization;
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
    /// </summary>
    internal class HunterTrackingPlugin : Singleton<HunterTrackingPlugin>, IModKitPlugin, ISaveablePlugin, IThreadedPlugin
    {
        [NotNull] private readonly HunterTrackingData data;
        private DateTime lastHighlightPass = DateTime.MinValue;
        private const double HighlightPassIntervalSeconds = 0.5;
        private const double HighlightDurationSeconds = 1.2;

        // Per-user set of animals currently highlighted, so highlights can be removed when an
        // animal leaves the range, dies or is removed from the tracked list.
        private readonly Dictionary<int, HashSet<Animal>> activeHighlights = new();

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
            HighlightPass();
        }

        private void HighlightPass()
        {
            foreach (var user in UserManager.Users)
            {
                if (user == null || !user.IsActive)
                    continue;

                if (!data.UserConfiguration.TryGetValue(user.Id, out var config) || !config.HighlightEnabled)
                    continue;

                if (user.ConstantHighlightSetting == null)
                    user.ConstantHighlightSetting = new ConstantHighlightSetting(user);

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
                var setting = user.ConstantHighlightSetting;
                foreach (var candidate in candidates)
                    setting.TryAddOrUpdate(candidate.Animal, HighlightPurpose.MasterTracker, HighlightDurationSeconds);

                if (activeHighlights.TryGetValue(user.Id, out var previous))
                {
                    foreach (var animal in previous)
                    {
                        if (!toHighlight.Contains(animal))
                            setting.TryRemove(animal, HighlightPurpose.MasterTracker);
                    }
                }

                activeHighlights[user.Id] = toHighlight;
            }
        }

        private void ClearHighlights(User user)
        {
            if (user.ConstantHighlightSetting == null)
                return;
            if (activeHighlights.TryGetValue(user.Id, out var previous))
            {
                foreach (var animal in previous)
                    user.ConstantHighlightSetting.TryRemove(animal, HighlightPurpose.MasterTracker);
                activeHighlights.Remove(user.Id);
            }
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
