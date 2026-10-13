using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.PathSystem;
using UnityEngine;

namespace TeleporterStation;

// the single-district rule at placement, previews only: DESIGN.md#one-network; mechanics: docs/game-internals.md, "Placement validation"
public class TeleporterPlacementValidator : IBlockObjectValidator
{
    private const string RefusalMessage =
        "Teleporters can only be placed on one district's paths.";

    private readonly IBlockService _blockService;
    private readonly EntityRegistry _entityRegistry;
    private readonly IPathService _pathService;
    private readonly DistrictCenterRegistry _districtCenterRegistry;

    private int _cacheFrame = -1;
    private int _hostDistrict = -1;
    private bool _multiHost;
    private readonly Dictionary<Vector3Int, int> _massDistricts = new();
    // entrance-only pieces: their single road connection, the tile their declared edge points at: DESIGN.md#one-network
    private readonly Dictionary<Vector3Int, Vector3Int> _specialEntrance = new();
    private readonly Dictionary<Vector3Int, int> _doorstepDistrict = new();

    public TeleporterPlacementValidator(IBlockService blockService,
        EntityRegistry entityRegistry, IPathService pathService,
        DistrictCenterRegistry districtCenterRegistry)
    {
        _blockService = blockService;
        _entityRegistry = entityRegistry;
        _pathService = pathService;
        _districtCenterRegistry = districtCenterRegistry;
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
        // a conflict holds both sides offline until one side is gone: DESIGN.md#one-network
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
        _specialEntrance.Clear();
        _doorstepDistrict.Clear();
        foreach (var center in _districtCenterRegistry.FinishedDistrictCenters)
        {
            if (center.District == null)
            {
                continue;
            }
            var entrance = center.GetComponent<BlockObject>()?.PositionedEntrance?.Coordinates;
            if (entrance == null)
            {
                continue;
            }
            var doorstep = center.CenterCoordinates;
            _specialEntrance[doorstep] = entrance.Value;
            _doorstepDistrict[doorstep] = center.District.CenterNodeId;
        }
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
            var stationEntrance = blockObject.PositionedEntrance?.Coordinates;
            foreach (var block in blockObject.PositionedBlocks.GetAllBlocks())
            {
                _specialEntrance[block.Coordinates] = stationEntrance ?? block.Coordinates;
            }
            var district = MassDistrict(blockObject);
            // the host is the SOLE district whose paths reach a teleporter: DESIGN.md#one-network
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

    // the mass's district: entrance-only pieces couple to the road world solely via their entrance tile; ordinary paths flood 4-way: docs/game-internals.md, "Placement validation"
    private int MassDistrict(BlockObject blockObject)
    {
        var start = default(Vector3Int);
        var hasStart = false;
        var ownEntrance = blockObject.PositionedEntrance?.Coordinates;
        var ownCells = new HashSet<Vector3Int>();
        foreach (var block in blockObject.PositionedBlocks.GetAllBlocks())
        {
            if (!hasStart)
            {
                start = block.Coordinates;
                hasStart = true;
            }
            ownCells.Add(block.Coordinates);
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
        foreach (var cell in ownCells)
        {
            queue.Enqueue(cell);
        }
        var district = -1;
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            if (!blob.Add(cell))
            {
                continue;
            }
            if (_doorstepDistrict.TryGetValue(cell, out var doorstepId))
            {
                district = doorstepId;
            }
            Vector3Int? partner = null;
            if (ownCells.Contains(cell))
            {
                partner = ownEntrance;
            }
            else if (_specialEntrance.TryGetValue(cell, out var knownPartner))
            {
                partner = knownPartner;
            }
            if (partner != null)
            {
                if (!blob.Contains(partner.Value))
                {
                    queue.Enqueue(partner.Value);
                }
                continue;
            }
            // an own cell without an entrance connects to nothing; never treat it as an ordinary path cell
            if (ownCells.Contains(cell) || !_pathService.IsPath(cell))
            {
                continue;
            }
            foreach (var neighbor in HorizontalNeighbors(cell))
            {
                if (blob.Contains(neighbor))
                {
                    continue;
                }
                // a doorstep or station tile is enterable only from the tile its entrance edge points at
                if (_specialEntrance.TryGetValue(neighbor, out var neighborEntrance))
                {
                    if (neighborEntrance == cell)
                    {
                        queue.Enqueue(neighbor);
                    }
                    continue;
                }
                // any other cell of a district center building is a wall
                if (DistrictCenterAt(neighbor) != null)
                {
                    continue;
                }
                if (_pathService.IsPath(neighbor))
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

    // orthogonal neighbors at same and neighboring heights; the rooftop hole is a known gap: DESIGN.md#known-gaps-and-accepted-rough-edges
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

    private DistrictCenter DistrictCenterAt(Vector3Int cell)
    {
        foreach (var blockObject in _blockService.GetObjectsAt(cell))
        {
            var center = blockObject.GetComponent<DistrictCenter>();
            if (center != null)
            {
                return center;
            }
        }
        return null;
    }
}
