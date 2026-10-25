using Timberborn.SingletonSystem;
using Timberborn.ZiplineSystem;

namespace TeleporterStation;

// restored pair cables stay unseen until their pair churns: DESIGN.md#travel-every-crossing-is-an-instant-jump
public class TeleporterCableVeil : ILoadableSingleton
{
    private readonly EventBus _eventBus;
    private readonly ZiplineCableRenderer _renderer;
    private readonly RingNetwork _rings;
    private readonly TeleporterNetwork _network;

    public TeleporterCableVeil(EventBus eventBus, ZiplineCableRenderer renderer,
        RingNetwork rings, TeleporterNetwork network)
    {
        _eventBus = eventBus;
        _renderer = renderer;
        _rings = rings;
        _network = network;
    }

    public void Load()
    {
        _eventBus.Register(this);
    }

    // every activation re-fires, load restores included, and the game re-shows on mirror activations, so each firing re-hides: DESIGN.md#travel-every-crossing-is-an-instant-jump
    [OnEvent]
    public void OnConnectionActivated(ZiplineConnectionActivatedEvent e)
    {
        var tower = e.ZiplineTower;
        if (!_rings.RingCoordsOf(tower, out _) && !_network.StationCoordsOf(tower, out _))
        {
            return;
        }
        foreach (var other in tower.ConnectionTargets)
        {
            if (_renderer._cableModels.TryGetValue(CableKey.Create(tower, other),
                    out var model))
            {
                model.SetVisibility(false);
            }
        }
    }
}
