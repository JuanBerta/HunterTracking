using Eco.Core.Serialization;
using Eco.Core.Utils;
using Eco.Shared.Networking;
using Eco.Shared.Serialization;
using Eco.Shared.Utils;

namespace HunterTracking
{
    [Serialized]
    public class HunterTrackingData : Singleton<HunterTrackingData>, IStorage
    {
        [Serialized] public ThreadSafeDictionary<int, UserConfig> UserConfiguration = new();
        public IPersistent? StorageHandle { get; set; }
    }

    [Serialized]
    public class UserConfig
    {
        [Serialized] public ThreadSafeList<string> TrackedSpecies { get; set; } = new();
        [Serialized] public float TrackingRange { get; set; } = 50f;
        [Serialized] public int MaxTrackedSpecies { get; set; } = 5;
        [Serialized] public int MaxHighlightedAnimals { get; set; } = 10;
        [Serialized] public bool HighlightEnabled { get; set; } = true;
    }
}
