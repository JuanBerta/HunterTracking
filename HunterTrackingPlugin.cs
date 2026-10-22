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
    /// reads their tracked species list and maintains, for every matching animal that is
    /// alive, active and inside the configured range:
    ///  1. a talent-free silhouette highlight on the animal (HighlightPurpose.Common), and
    ///  2. a personal green map marker (WorldMarker) rendered on the in-game M-key map.
    /// Animals that no longer match are un-highlighted / un-marked on the next pass.
    ///
    /// Rendering note (verified against Eco.ReferenceAssemblies 0.14.2.1 + official ModKit
    /// docs): ConstantHighlightSetting is the only server-side highlight channel; it is a
    /// generic per-user list of "objects to keep highlighted on the client" and is NOT gated
    /// behind any talent. HighlightPurpose.MasterTracker is only fed by the in-game
    /// MasterTracker talent, which is why a player without that skill never received any
    /// highlight entry and saw no silhouette. The fix therefore uses HighlightPurpose.Common
    /// (the enum value explicitly documented as the generic, non-talent purpose) with
    /// timeToHighlight &lt;= 0 (infinite): the client renders the silhouette immediately for
    /// every user and keeps it until TryRemove is called.
    ///
    /// 0.1.7 map-marker layer: a green silhouette can be hidden by line-of-sight / client
    /// sync limitations, so 0.1.7 (re)introduces the personal WorldMarker layer first
    /// shipped in 0.1.2 and reverted in 0.1.3 (because at the time the requirement was a
    /// real-world indicator - WorldMarker renders on the M-key map, not in the world).
    /// For 0.1.7 the requirement is exactly that map layer: every tracked animal inside
    /// range gets a personal neon-green marker on the M-key map that follows the animal
    /// (SetPos every pass), shows the species name, and is removed when the animal dies,
    /// leaves the range, is removed from the tracked list or highlighting is turned off.
    /// Fixed vs 0.1.2: SetDisplayOffScreen is NOT used anymore - the off-screen arrow
    /// indicator was meant for a real-world layer and would be noise now that the marker
    /// is a pure map-layer feature; all other fields (SetPos, SetColor, SetDisplayText,
    /// SetDisplayName, SetMaxDistanceToShow, MakeMarkerPersonal, MarkerFolderName.None)
    /// match the 0.1.2 implementation that was verified working on the map.
    /// The HighlightPurpose.Common silhouette channel is retained: it does not conflict
    /// with the map markers and keeps the 0.1.6 behaviour fully unchanged.
    ///
    /// Interaction-channel note (investigated for 0.1.3): the green silhouette shown on
    /// interactable objects is driven by ServerInteractionManager's registration tables
    /// (NetObjectNameToInteractions / InteractorToInteractions / ComponentToInteractions).
    /// Those registrations are type-level and global: adding a DisplayOnly interaction for
    /// Eco.Simulation.Agents.Animal would make EVERY alive animal interactable for EVERY
    /// player (anyone looking at it), with no per-player / per-animal filtering, and it is
    /// still gated by line-of-sight + interaction distance. That cannot express "only the
    /// animals this user tracks", so the per-user ConstantHighlightSetting channel remains
    /// the correct primitive for a tracking mod.
    ///
    /// 0.1.4 custom-rendering research (verified 2026-10-21): the public ModKit cannot
    /// inject client-side code at all. (a) Eco's client loads only asset bundles
    /// (.unity3d: meshes/icons/animations) streamed by the server; it never loads a mod's
    /// own C# DLL. (b) There is no IModKitClientPlugin / client-side plugin interface -
    /// IModKitPlugin / IInitializablePlugin / IShutdownablePlugin / IThreadedPlugin are all
    /// server-side only. (c) Eco.ReferenceAssemblies 0.14.2.1 contains no Client namespace
    /// and no assembly references UnityEngine, so Graphics.DrawMesh / CommandBuffer /
    /// OnRenderObject / custom MonoBehaviour are simply absent from the modding surface.
    /// (d) BepInEx/Harmony have no supported path because the Eco client ships as a
    /// self-contained build. Therefore a through-object green silhouette drawn by custom
    /// client code is NOT achievable through official channels; the practical, supported,
    /// talent-free fallback shipped in 0.1.4 is /ht locate, which reports the nearest
    /// tracked animal's bearing, distance and world coordinates using server-side APIs
    /// (World.WrappedDistance / World.ClosestWrappedLocation / Animal.Position /
    /// User.Position).
    ///
    /// 0.1.6 (previous release): the 0.1.5 client asset-bundle override
    /// (HunterTracking.unity3d: 26 animal prefabs with
    /// ReuseVanillaPrefab.SourceName = species short name + HighlightableObject
    /// seeThrough = true) made every matching animal invisible on the client, even at
    /// its exact location, while the entity logic kept running server-side. Root cause:
    /// the client matched those prefabs as the animal render source, but the
    /// ReuseVanillaPrefab reuse mechanism does not resolve vanilla animal render assets
    /// (it resolves vanilla WorldObject prefabs by name, and animals are not
    /// WorldObjects), so the empty override prefab was rendered instead of the vanilla
    /// animal - nothing visible. HighlightableObject was not the cause of the
    /// disappearance (it only adds a highlight marker), but the whole bundle-override
    /// approach cannot be made safe in the short term, so v0.1.6 reverts the override:
    /// no .unity3d is shipped, the client renders vanilla animals normally again, and
    /// the HighlightPurpose.Common pass + /ht locate remain the supported highlight
    /// fallback (see 0.1.4 research above). 0.1.7 keeps that revert and adds the
    /// M-key map marker layer described above.
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

        // Per-user set of animals currently highlighted (Common purpose), so highlights can
        // be removed when an animal leaves the range, dies or is removed from the tracked
        // list.
        private readonly Dictionary<int, HashSet<Animal>> activeHighlights = new();
        // Per-user map markers (WorldMarker) keyed by animal ID, mirroring activeHighlights.
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
