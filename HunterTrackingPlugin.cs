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
    /// Hunter Tracking main plugin. Every server tick (throttled to 0.5 s) it walks all
    /// online users, reads their tracked species list and maintains, for every matching
    /// animal that is alive, active and inside the configured range, a personal map marker
    /// (WorldMarker) rendered on the in-game M-key map and minimap. Markers follow the
    /// animal live (SetPos every pass), show the species name and are removed on the next
    /// pass when the animal dies, leaves the range or is removed from the tracked list.
    ///
    /// 0.1.8 fix (why 0.1.7 showed nothing): the 0.1.7 build fed TWO channels and neither
    /// rendered on a live client.
    ///  1. HighlightPurpose.Common silhouettes (ConstantHighlightSetting.TryAddOrUpdate)
    ///     were confirmed impossible server-side: the client renders that outline only for
    ///     entities it has synced AND only through the in-game MasterTracker talent flow;
    ///     the public ModKit cannot inject client rendering code (see 0.1.4 research), so
    ///     the talent-free Common feed never produced a visible silhouette. This channel is
    ///     REMOVED in 0.1.8.
    ///  2. The WorldMarker map layer was created without calling SetVisiblity. The marker
    ///     still had to be explicitly told which visibility it uses - WorldMarkerVisibility
    ///     is a [Flags] enum whose flags are opt-in at creation time on some clients - so
    ///     the marker was created but the client did not register it for rendering. 0.1.8
    ///     calls SetVisiblity(WorldMarkerVisibility.ShowInTheWorldAndMinimap) right after
    ///     creation on every marker.
    /// The remaining differences vs 0.1.7 are cosmetic (label text, command wording);
    /// marker creation, per-user personal scope (WorldMarkerUtils.MakeMarkerPersonal),
    /// 0.5 s SetPos follow, SetMaxDistanceToShow range capping and removal semantics are
    /// kept from 0.1.7.
    ///
    /// Asset-bundle note (verified against Eco.ReferenceAssemblies 0.14.2.1 + the official
    /// ModKit): WorldMarker.IconNameOverride is get-only, so a server mod cannot assign a
    /// custom icon name; the client looks icons up in its own built-in icon library
    /// (StreamingAssets/aa/StandaloneWindows64/icons_assets_all_*.bundle) keyed by the
    /// marker type. A separate HunterTracking icon bundle is therefore NOT required for map
    /// markers and - critically - the mod must NEVER ship an animal prefab override again
    /// (0.1.5 proved ReuseVanillaPrefab cannot resolve vanilla animal render assets and
    /// makes animals invisible). 0.1.8 ships no animal prefab bundle; the optional icon
    /// asset bundle built with the Unity CLI is an offline asset-pipeline verification
    /// artifact only and contains sprites, never prefabs (see README 0.1.8 section).
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
    /// talent-free fallback is /ht locate, which reports the nearest tracked animal's
    /// bearing, distance and world coordinates using server-side APIs (World.WrappedDistance
    /// / World.ClosestWrappedLocation / Animal.Position / User.Position).
    ///
    /// World-layer note (verified for 0.1.8): a real-world floating marker
    /// (MinimapComponent / MinimapObject) is NOT attachable to animals - MinimapComponent
    /// is a WorldObjectComponent and animals are not WorldObjects (0.1.5 root cause), so
    /// the only server-driven marker channel that works for animals is the map layer
    /// (WorldMarker on the M-key map + minimap).
    /// </summary>
    internal class HunterTrackingPlugin : Singleton<HunterTrackingPlugin>, IModKitPlugin, ISaveablePlugin, IThreadedPlugin
    {
        [NotNull] private readonly HunterTrackingData data;
        private DateTime lastMarkerPass = DateTime.MinValue;
        private const double MarkerPassIntervalSeconds = 0.5;

        // Per-user map markers (WorldMarker) keyed by animal ID.
        private readonly Dictionary<int, Dictionary<int, WorldMarker>> activeMarkers = new();
        private readonly object markersLock = new();

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
            if ((now - lastMarkerPass).TotalSeconds < MarkerPassIntervalSeconds)
                return;
            lastMarkerPass = now;
            try
            {
                MarkerPass();
            }
            catch (Exception ex)
            {
                Log.WriteErrorLineLoc($"HunterTracking marker pass failed: {ex.Message}");
            }
        }

        private void MarkerPass()
        {
            var totalMarked = 0;
            foreach (var user in UserManager.Users)
            {
                if (user == null || !user.IsActive || user.Markers == null || user.Player == null)
                    continue;

                if (!data.UserConfiguration.TryGetValue(user.Id, out var config) || !config.HighlightEnabled)
                {
                    ClearMarkers(user);
                    continue;
                }

                var wanted = new HashSet<string>(config.TrackedSpecies, StringComparer.OrdinalIgnoreCase);
                if (wanted.Count == 0)
                {
                    ClearMarkers(user);
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

                var toMark = candidates.Select(x => x.Animal).ToHashSet();
                foreach (var candidate in candidates)
                    UpdateMarker(user, candidate.Animal, range);

                Dictionary<int, WorldMarker>? previous;
                lock (markersLock)
                {
                    activeMarkers.TryGetValue(user.Id, out previous);
                }
                if (previous != null)
                {
                    foreach (var animalId in previous.Keys.ToList())
                    {
                        if (!toMark.Any(a => a.ID == animalId))
                        {
                            RemoveMarker(user, previous[animalId]);
                            lock (markersLock)
                            {
                                previous.Remove(animalId);
                                if (previous.Count == 0)
                                    activeMarkers.Remove(user.Id);
                            }
                        }
                    }
                }

                totalMarked += toMark.Count;
            }

            if (totalMarked > 0)
                Log.Debug($"HunterTracking: marker pass complete, {totalMarked} animal(s) marked.");
        }

        private void UpdateMarker(User user, Animal animal, float range)
        {
            var player = user.Player;
            if (player == null || user.Markers == null)
                return;

            var speciesName = animal.Species?.Name ?? "Animal";
            var pos = ToVector3i(animal.GroundPosition);

            lock (markersLock)
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
                    // 0.1.8 fix: without an explicit SetVisiblity the client did not
                    // register the marker for rendering on live servers (0.1.7 failure).
                    marker.SetVisiblity(WorldMarkerVisibility.ShowInTheWorldAndMinimap);
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

        private void RemoveMarker(User user, WorldMarker marker)
        {
            var player = user.Player;
            if (player == null)
                return;

            try
            {
                marker.Remove(player);
            }
            catch (Exception ex)
            {
                Log.Debug($"HunterTracking: marker removal failed: {ex.Message}");
            }
        }

        private void ClearMarkers(User user)
        {
            Dictionary<int, WorldMarker>? markers;
            lock (markersLock)
            {
                activeMarkers.TryGetValue(user.Id, out markers);
                if (markers != null)
                    activeMarkers.Remove(user.Id);
            }

            if (markers != null && user.Player != null)
            {
                foreach (var marker in markers.Values)
                {
                    try { marker.Remove(user.Player); } catch { }
                }
            }
        }

        /// <summary>Number of map markers currently maintained for a user (diagnostic for /ht list).</summary>
        public int GetActiveMarkerCount(int userId)
        {
            lock (markersLock)
            {
                return activeMarkers.TryGetValue(userId, out var dict) ? dict.Count : 0;
            }
        }

        private static Vector3i ToVector3i(Vector3 v)
        {
            return new Vector3i((int)Math.Floor(v.X), (int)Math.Floor(v.Y), (int)Math.Floor(v.Z));
        }

        public Task ShutdownAsync() => Task.CompletedTask;
    }
}
