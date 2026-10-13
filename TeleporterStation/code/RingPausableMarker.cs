using Timberborn.BaseComponentSystem;
using Timberborn.Buildings;

namespace TeleporterStation;

// the built half alone is pausable; the remote inherits the flip through the route: DESIGN.md#pause
public class RingPausableMarker : BaseComponent, IFinishedPausable { }
