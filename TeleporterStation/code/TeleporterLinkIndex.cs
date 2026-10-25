using System;
using System.Collections.Generic;
using Timberborn.BlockSystem;
using Timberborn.Common;
using Timberborn.Coordinates;
using Timberborn.Navigation;
using Timberborn.ZiplineSystem;
using UnityEngine;

namespace TeleporterStation;

// the shared link/sibling/matching layer of both teleport systems: DESIGN.md#travel-every-crossing-is-an-instant-jump
public abstract class TeleporterLinkIndex
{
    private protected readonly INavMeshService _navMeshService;
    private readonly NavMeshGroupService _navMeshGroupService;
    private readonly IBlockService _blockService;
    private readonly BresenhamLineDrawer _bresenhamLines;

    private readonly HashSet<(Vector3Int, Vector3Int)> _pairs = new();
    private (Vector3Int, Vector3Int)? _probe;

    // live-pair O(1) index for route status: docs/performance.md
    private readonly Dictionary<Vector3Int, HashSet<Vector3Int>> _siblings = new();

    internal int GroupId { get; private set; } = -1;

    internal string GroupName { get; private set; }

    // the routing cost this system writes onto its own edges: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal virtual float EdgeCost => 0f;

    // the plate the flight lifts from and lands on: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal virtual Vector3 AnchorWorld(Vector3Int coordinates)
    {
        return CoordinateSystem.GridToWorldCentered(coordinates)
            + new Vector3(0f, 0.05f, 0f);
    }

    // live link count (one pair = one link)
    internal int LinkCount => _pairs.Count;

    // load-window flag: DESIGN.md#load-window
    internal bool LoadComplete { get; private protected set; }

    protected TeleporterLinkIndex(INavMeshService navMeshService,
        NavMeshGroupService navMeshGroupService, IBlockService blockService,
        BresenhamLineDrawer bresenhamLines)
    {
        _navMeshService = navMeshService;
        _navMeshGroupService = navMeshGroupService;
        _blockService = blockService;
        _bresenhamLines = bresenhamLines;
    }

    internal void ResolveGroup(string groupName)
    {
        GroupName = groupName;
        GroupId = _navMeshGroupService.GetOrAddGroupId(groupName);
    }

    // the edge diff: adds and removes until the live pairs match desired
    internal (int added, int removed) ApplyLinks(HashSet<(Vector3Int, Vector3Int)> desired)
    {
        var removed = 0;
        var stale = new List<(Vector3Int, Vector3Int)>();
        foreach (var pair in _pairs)
        {
            if (!desired.Contains(pair))
            {
                stale.Add(pair);
            }
        }
        foreach (var pair in stale)
        {
            if (WritePair(pair.Item1, pair.Item2, add: false))
            {
                _pairs.Remove(pair);
                removed++;
            }
        }
        var added = 0;
        foreach (var pair in desired)
        {
            if (_pairs.Contains(pair))
            {
                continue;
            }
            if (WritePair(pair.Item1, pair.Item2, add: true))
            {
                _pairs.Add(pair);
                added++;
            }
        }
        if (added > 0 || removed > 0)
        {
            RebuildSiblingIndex();
        }
        return (added, removed);
    }

    // the physical pair write: each system writes its own instant span edges: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal abstract bool WritePair(Vector3Int start, Vector3Int end, bool add);

    // any live edge touches this cell (route liveness for a ring)
    internal bool HasAnyLink(Vector3Int coordinates)
    {
        return _siblings.TryGetValue(coordinates, out var set) && set.Count > 0;
    }

    // preview-only probe edge so the game's district-merge guard gates the drag: DESIGN.md#ring-pairs
    internal void SetProbeEdge(Vector3Int? start, Vector3Int? end)
    {
        (Vector3Int, Vector3Int)? next = null;
        if (start.HasValue && end.HasValue && start.Value != end.Value)
        {
            next = Canonical(start.Value, end.Value);
        }
        if (next == _probe)
        {
            return;
        }
        if (_probe.HasValue)
        {
            ChangeProbeEdge(_probe.Value, add: false);
            _probe = null;
        }
        if (next.HasValue)
        {
            ChangeProbeEdge(next.Value, add: true);
            _probe = next;
        }
    }

    private void ChangeProbeEdge((Vector3Int, Vector3Int) pair, bool add)
    {
        var (a, b) = pair;
        var edge = NavMeshEdge.CreateGrouped(a, b, GroupId, isRoad: true, EdgeCost);
        if (add)
        {
            _navMeshService.AddPreviewEdge(edge);
            _navMeshService.AddPreviewEdge(
                NavMeshEdge.CreateGrouped(b, a, GroupId, isRoad: true, EdgeCost));
        }
        else
        {
            _navMeshService.RemovePreviewEdge(edge);
            _navMeshService.RemovePreviewEdge(
                NavMeshEdge.CreateGrouped(b, a, GroupId, isRoad: true, EdgeCost));
        }
    }

    // the tracker bills the system that owns the entry cell
    internal abstract void BillCrossing(Vector3Int entry);

    // towers resolve to their system's coordinates for ownership checks
    internal abstract bool CoordsOf(ZiplineTower tower, out Vector3Int coordinates);

    // a live pair of THIS system, span edge or leftover game cable alike: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal bool OwnsLink(ZiplineTower towerA, ZiplineTower towerB)
    {
        return CoordsOf(towerA, out var a) && CoordsOf(towerB, out var b)
               && _siblings.TryGetValue(a, out var set) && set.Contains(b);
    }

    // the game's own load re-connect runs uncarved and leaves occupancy blocks on the line: DESIGN.md#ring-pairs
    protected void SweepConnectionBlocks(IEnumerable<ZiplineTower> towers,
        Func<ZiplineTower, bool> owned)
    {
        foreach (var tower in towers)
        {
            foreach (var other in tower.ConnectionTargets)
            {
                if (owned(other))
                {
                    SweepPair(tower, other);
                }
            }
        }
    }

    // DeleteEntity only unsets occupancy; the object survives so the game's Disconnect cleanup keeps working
    private void SweepPair(ZiplineTower towerA, ZiplineTower towerB)
    {
        var line = new HashSet<Vector3Int>();
        _bresenhamLines.DrawLine(towerA.CableAnchorPoint.FloorToInt(),
            towerB.CableAnchorPoint.FloorToInt(), line);
        foreach (var cell in line)
        {
            var block = _blockService.GetBottomObjectComponentAt<CableBlock>(cell);
            if (block == null)
            {
                continue;
            }
            block.GetComponent<BlockObject>().DeleteEntity();
        }
    }

    private void RebuildSiblingIndex()
    {
        _siblings.Clear();
        foreach (var (a, b) in _pairs)
        {
            AddSibling(a, b);
            AddSibling(b, a);
        }
    }

    private void AddSibling(Vector3Int station, Vector3Int sibling)
    {
        if (!_siblings.TryGetValue(station, out var set))
        {
            set = new HashSet<Vector3Int>();
            _siblings[station] = set;
        }
        set.Add(sibling);
    }

    internal static (Vector3Int, Vector3Int) Canonical(Vector3Int a, Vector3Int b)
    {
        return (a.x, a.y, a.z).CompareTo((b.x, b.y, b.z)) <= 0 ? (a, b) : (b, a);
    }
}
