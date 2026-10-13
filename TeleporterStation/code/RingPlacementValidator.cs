using Timberborn.BlockSystem;
using Timberborn.Localization;

namespace TeleporterStation;

// remote-placement rules at the game's own placement seam, previews only: DESIGN.md#ring-pairs
public class RingPlacementValidator : IBlockObjectValidator
{
    private readonly RingPairingState _state;
    private readonly ILoc _loc;

    public RingPlacementValidator(RingPairingState state, ILoc loc)
    {
        _state = state;
        _loc = loc;
    }

    public bool IsValid(BlockObject blockObject, out string errorMessage)
    {
        errorMessage = null;
        if (!blockObject.IsPreview)
        {
            return true;
        }
        var ring = blockObject.GetComponent<RingNode>();
        if (ring == null)
        {
            return true;
        }
        var anchor = _state.Anchor;
        if (ring.IsRemote && (anchor == null || anchor.IsRemote))
        {
            errorMessage = _loc.T("Ring.Placement.NeedBuilt");
            return false;
        }
        if (ring.IsRemote && (!anchor.IsFinished || !anchor.IsCharged))
        {
            errorMessage = _loc.T("Ring.Placement.NeedReady");
            return false;
        }
        if (anchor == null)
        {
            return true;
        }
        foreach (var block in blockObject.PositionedBlocks.GetAllBlocks())
        {
            if (!RingNode.InRange(anchor.Coordinates, block.Coordinates))
            {
                errorMessage = _loc.T("Ring.Placement.OutOfRange");
                return false;
            }
        }
        return true;
    }
}
