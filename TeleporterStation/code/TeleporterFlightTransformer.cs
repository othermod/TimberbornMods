using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.Common;
using Timberborn.Coordinates;
using Timberborn.Navigation;
using Timberborn.ZiplineSystem;
using UnityEngine;

namespace TeleporterStation;

// rewrites both systems' span edges into instant anchor jumps; corners carry their system's own group: DESIGN.md#travel-every-crossing-is-an-instant-jump
public class TeleporterFlightTransformer : IPathTransformer
{
    private readonly TeleporterNetwork _network;

    private readonly RingNetwork _rings;

    private readonly IBlockService _blockService;

    public TeleporterFlightTransformer(TeleporterNetwork network, RingNetwork rings,
        IBlockService blockService)
    {
        _network = network;
        _rings = rings;
        _blockService = blockService;
    }

    public bool Transform(ref int index, ReadOnlyList<FlowFieldPathNode> flowFieldPath,
        List<PathCorner> pathCorners)
    {
        if (index >= flowFieldPath.Count - 1)
        {
            return false;
        }
        var from = flowFieldPath[index];
        // each system's span group is exclusively its own; the game's zipline transformer claims only its regular group: DESIGN.md#travel-every-crossing-is-an-instant-jump
        if (from.GroupId != _network.GroupId && from.GroupId != _rings.GroupId)
        {
            return false;
        }
        var to = flowFieldPath[index + 1];
        var entry = TowerAt(from.Position);
        if (entry == null)
        {
            return false;
        }
        var exit = TowerAt(to.Position);
        if (exit == null || exit == entry)
        {
            return false;
        }
        var owned = from.GroupId == _network.GroupId
            ? _network.OwnsLink(entry, exit)
            : _rings.OwnsLink(entry, exit);
        if (!owned)
        {
            return false;
        }
        // MaxValue snaps without spending tick time, the game's own zero-cost step: DESIGN.md#crossing-detection-and-billing
        pathCorners.Add(new PathCorner(from.Position, float.MaxValue, from.GroupId));
        pathCorners.Add(new PathCorner(to.Position, float.MaxValue, from.GroupId));
        index++;
        return true;
    }

    // the game's own recipe: the corner's floored cell must be the tower's anchor cell: docs/game-internals.md
    private ZiplineTower TowerAt(Vector3 position)
    {
        var cell = CoordinateSystem.WorldToGridInt(position);
        var tower = _blockService.GetBottomObjectComponentAt<ZiplineTower>(cell);
        return tower != null && cell == tower.CableAnchorPointInt ? tower : null;
    }
}
