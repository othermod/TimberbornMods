using System.Collections.Generic;
using Timberborn.Common;
using Timberborn.Coordinates;
using Timberborn.Navigation;
using UnityEngine;

namespace TeleporterStation;

// rewrites every crossing, station or ring, into the anchor-to-anchor flight: DESIGN.md#travel-timed-crossings
public class TeleporterPathTransformer : IPathTransformer
{
    private readonly TeleporterNetwork _network;
    private readonly RingNetwork _rings;

    public TeleporterPathTransformer(TeleporterNetwork network, RingNetwork rings)
    {
        _network = network;
        _rings = rings;
    }

    public bool Transform(ref int index, ReadOnlyList<FlowFieldPathNode> flowFieldPath,
        List<PathCorner> pathCorners)
    {
        if (index >= flowFieldPath.Count - 1)
        {
            return false;
        }
        var from = flowFieldPath[index];
        if (from.Cost <= 0f)
        {
            return false;
        }
        var to = flowFieldPath[index + 1];
        var owner = OwnerOf(from.GroupId);
        if (owner == null)
        {
            // fabricated single-edge legs carry group 0: match the link by its cells: DESIGN.md#travel-timed-crossings
            owner = OwnerByCells(NavigationCoordinateSystem.WorldToGridInt(from.Position),
                NavigationCoordinateSystem.WorldToGridInt(to.Position));
        }
        if (owner == null)
        {
            return false;
        }
        var entryCell = NavigationCoordinateSystem.WorldToGridInt(from.Position);
        var exitCell = NavigationCoordinateSystem.WorldToGridInt(to.Position);
        if (!LinkedPair(owner, entryCell, exitCell))
        {
            return false;
        }
        var entryAnchor = owner.AnchorWorld(entryCell);
        var exitAnchor = owner.AnchorWorld(exitCell);
        var speed = owner.TransitSpeed(entryCell, exitCell);
        // ground tile corners bookend the flight so the vanish happens at the plate: DESIGN.md#travel-timed-crossings
        pathCorners.Add(new PathCorner(from.Position, speed, owner.GroupId));
        pathCorners.Add(new PathCorner(entryAnchor, speed, owner.GroupId));
        pathCorners.Add(new PathCorner(exitAnchor, speed, owner.GroupId));
        pathCorners.Add(new PathCorner(to.Position, speed, owner.GroupId));
        index++;
        return true;
    }

    private TeleporterLinkIndex OwnerOf(int groupId)
    {
        if (groupId == _network.GroupId)
        {
            return _network;
        }
        if (groupId == _rings.GroupId)
        {
            return _rings;
        }
        return null;
    }

    private TeleporterLinkIndex OwnerByCells(Vector3Int entry, Vector3Int exit)
    {
        if (Linked(_network, entry, exit))
        {
            return _network;
        }
        if (Linked(_rings, entry, exit))
        {
            return _rings;
        }
        return null;
    }

    private static bool Linked(TeleporterLinkIndex owner, Vector3Int a, Vector3Int b)
    {
        return owner.IsLinked(a, b) || owner.IsLinked(b, a);
    }

    private static bool LinkedPair(TeleporterLinkIndex owner, Vector3Int entry, Vector3Int exit)
    {
        return owner.IsLinked(entry, exit) || owner.IsLinked(exit, entry);
    }
}
