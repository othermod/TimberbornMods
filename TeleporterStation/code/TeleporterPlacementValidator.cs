using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.PathSystem;
using UnityEngine;

namespace TeleporterStation;

// The single-district rule, installed through the game's own placement
// pipeline (DESIGN.md#one-network): all teleporters live on one district.
// The game refuses any path that would road-connect two districts, but
// nothing stops a player from placing teleporters on two districts'
// separate path systems; this validator does. Once any teleporter stands
// on a district's paths (unbuilt, built, filled or dry: any placed
// teleporter counts, previews do not), another teleporter's ghost turns
// red on every other district's paths. Wilderness (unclaimed ground)
// stays legal: those stations join the network through the wormhole. A
// save made by a multi-district version converges by the player's hand:
// with teleporters standing on several districts (a conflict), every
// network stays offline and placements are refused on both sides until
// one district has no teleporters left; whatever remains then joins the
// sole survivor's network automatically.
//
// Mechanics, all verified against decompiled game code: placement ghosts
// and commits are gated by BlockObjectValidationService, which runs every
// IBlockObjectValidator (MultiBind-collected, the same DI mechanism this
// mod's configurator already uses); refusing turns the ghost red and
// blocks the commit, no Harmony. Veto only placement GHOSTS of
// TELEPORTERS: the game also runs this check for internal object swaps
// (a cut dead bush re-enters the service through the same check; live
// crash: a canola between two network territories threw
// InvalidOperationException out of a bot's cutting tick) and at load
// (AddToServiceAfterLoad DELETES non-conforming loaded objects; a
// teleporter in an old multi-district save must not be demolished).
//
// The evidence is world state, not nav maps, because the game does not
// apply new paths to its nav data while paused (the pipeline stalls until
// resume; live report 2026-10-07: a bridge placed piece by piece right
// after a load read as "no territories connected" from the frozen claim
// maps). The candidate's road mass is flood-filled through the game's
// own road discriminator (IPathService.IsPath: placed grid, then preview
// grid, both world data, so the answer holds while paused) plus teleporter
// tiles (a station is walkable road too, and the one world piece the
// road flood must cross; the wormhole links are nav-only and cannot leak
// across), and the mass's district is whichever finished district center
// touches it. Placement-time only; verdict cached per frame.
public class TeleporterPlacementValidator : IBlockObjectValidator
{
    private const string RefusalMessage =
        "Teleporters can only be placed on one district's paths.";

    private readonly IBlockService _blockService;
    private readonly EntityRegistry _entityRegistry;
    private readonly IPathService _pathService;

    private int _cacheFrame = -1;
    private int _hostDistrict = -1;
    private bool _multiHost;
    private readonly Dictionary<Vector3Int, int> _massDistricts = new();
    private readonly HashSet<Vector3Int> _stationCells = new();

    public TeleporterPlacementValidator(IBlockService blockService,
        EntityRegistry entityRegistry, IPathService pathService)
    {
        _blockService = blockService;
        _entityRegistry = entityRegistry;
        _pathService = pathService;
    }

    public bool IsValid(BlockObject blockObject, out string errorMessage)
    {
        errorMessage = null;
        if (!blockObject.IsPreview
            || blockObject.GetComponent<TeleporterNode>() == null)
        {
            return true;
        }
        CacheForFrame();
        if (_hostDistrict == -1)
        {
            return true;
        }
        var district = MassDistrict(blockObject);
        // a conflict (teleporters on several districts: only a save from a
        // multi-district version can make one) holds everything offline
        // until the player deletes one district's teleporters whole, so
        // neither side may be extended; wilderness ground stays legal
        if (_multiHost)
        {
            if (district == -1)
            {
                return true;
            }
            errorMessage = RefusalMessage;
            return false;
        }
        if (district == -1 || district == _hostDistrict)
        {
            return true;
        }
        errorMessage = RefusalMessage;
        return false;
    }

    private void CacheForFrame()
    {
        var frame = Time.frameCount;
        if (frame == _cacheFrame)
        {
            return;
        }
        _cacheFrame = frame;
        _hostDistrict = -1;
        _multiHost = false;
        _massDistricts.Clear();
        _stationCells.Clear();
        foreach (var entity in _entityRegistry.Entities)
        {
            if (entity.GetComponent<TeleporterNode>() == null)
            {
                continue;
            }
            var blockObject = entity.GetComponent<BlockObject>();
            if (blockObject == null || blockObject.IsPreview)
            {
                continue;
            }
            foreach (var block in blockObject.PositionedBlocks.GetAllBlocks())
            {
                _stationCells.Add(block.Coordinates);
            }
            var district = MassDistrict(blockObject);
            // the host is the SOLE district whose paths reach a
            // teleporter; two or more is a conflict, the same rule the
            // network applies (TeleporterNetwork.RefreshLinks): all
            // networks stay offline and nothing may be placed on either
            // side until one district has no teleporters left
            if (district != -1)
            {
                if (_hostDistrict == -1)
                {
                    _hostDistrict = district;
                }
                else if (_hostDistrict != district)
                {
                    _multiHost = true;
                }
            }
        }
    }

    // the district of the road mass the object stands in: flood from the
    // object's cells through path pieces until a finished district center
    // touches the mass; -1 when none does (wilderness). Memoized per
    // frame at every visited cell, so teleporters on one shared mass
    // flood it once.
    private int MassDistrict(BlockObject blockObject)
    {
        var start = default(Vector3Int);
        var hasStart = false;
        foreach (var block in blockObject.PositionedBlocks.GetAllBlocks())
        {
            start = block.Coordinates;
            hasStart = true;
            break;
        }
        if (!hasStart)
        {
            return -1;
        }
        if (_massDistricts.TryGetValue(start, out var known))
        {
            return known;
        }
        var blob = new HashSet<Vector3Int>();
        var queue = new Queue<Vector3Int>();
        foreach (var block in blockObject.PositionedBlocks.GetAllBlocks())
        {
            queue.Enqueue(block.Coordinates);
        }
        var district = -1;
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            if (!blob.Add(cell))
            {
                continue;
            }
            foreach (var neighbor in HorizontalNeighbors(cell))
            {
                if (blob.Contains(neighbor))
                {
                    continue;
                }
                var centerId = DistrictCenterAt(neighbor);
                if (centerId != -1)
                {
                    district = centerId;
                    continue;
                }
                if (_pathService.IsPath(neighbor) || _stationCells.Contains(neighbor))
                {
                    queue.Enqueue(neighbor);
                }
            }
        }
        foreach (var cell in blob)
        {
            _massDistricts[cell] = district;
        }
        return district;
    }

    // Grid Vector3Int(x, y, z) maps to world (x, worldZ=y, height=z):
    // horizontal orthogonal neighbors differ by 1 in the x OR y component
    // (never both), each checked at the same, one-below, and one-above
    // HEIGHT (z): road/stair geometry. Anything wider would only produce
    // false refusals. (Known hole, inherited from the game's own guard:
    // the flood crosses path pieces but not building rooftops, so a chain
    // routed across a rooftop splits into two masses.)
    private static IEnumerable<Vector3Int> HorizontalNeighbors(Vector3Int cell)
    {
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (System.Math.Abs(dx) + System.Math.Abs(dy) != 1)
                {
                    continue;
                }
                for (var dz = -1; dz <= 1; dz++)
                {
                    yield return new Vector3Int(cell.x + dx, cell.y + dy, cell.z + dz);
                }
            }
        }
    }

    private int DistrictCenterAt(Vector3Int cell)
    {
        foreach (var blockObject in _blockService.GetObjectsAt(cell))
        {
            var center = blockObject.GetComponent<DistrictCenter>();
            if (center != null && center.District != null)
            {
                return center.District.CenterNodeId;
            }
        }
        return -1;
    }
}
