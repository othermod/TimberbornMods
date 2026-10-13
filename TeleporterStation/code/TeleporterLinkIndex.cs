using System;
using System.Collections.Generic;
using Timberborn.Coordinates;
using Timberborn.Navigation;
using UnityEngine;

namespace TeleporterStation;

// the shared edge/sibling/matching layer of both teleport systems: DESIGN.md#travel-timed-crossings
public abstract class TeleporterLinkIndex
{
    private protected readonly INavMeshService _navMeshService;
    private readonly NavMeshGroupService _navMeshGroupService;
    private protected readonly NodeIdService _nodeIdService;
    private readonly TerrainNavMeshSource _terrainNavMeshSource;
    private readonly InstantTerrainNavMeshSource _instantTerrainNavMeshSource;
    private readonly PreviewTerrainNavMeshSource _previewTerrainNavMeshSource;
    private readonly RoadNavMeshSource _roadNavMeshSource;
    private readonly InstantRoadNavMeshSource _instantRoadNavMeshSource;
    private readonly PreviewRoadNavMeshSource _previewRoadNavMeshSource;

    private readonly Dictionary<(Vector3Int, Vector3Int), NavMeshEdge> _edges = new();
    private readonly HashSet<(Vector3Int, Vector3Int)> _pairs = new();
    private (Vector3Int, Vector3Int)? _probe;

    // MatchLiveLink O(1) index: docs/performance.md
    private readonly Dictionary<Vector3Int, HashSet<Vector3Int>> _siblings = new();

    // connected ground under the flight line: (edge, sharer count) per direction: DESIGN.md#travel-timed-crossings
    private readonly Dictionary<(Vector3Int, Vector3Int), (NavMeshEdge Edge, int Sharers)> _chainEdges = new();

    internal int GroupId { get; private set; } = -1;

    internal string GroupName { get; private set; }

    // the cost of a live edge in walk-seconds; drives both routing and raw corner speeds: DESIGN.md#travel-timed-crossings
    internal virtual float EdgeCost(Vector3Int start, Vector3Int end)
    {
        return 0f;
    }

    // the plate the flight lifts from and lands on: DESIGN.md#travel-timed-crossings
    internal virtual Vector3 AnchorWorld(Vector3Int coordinates)
    {
        return CoordinateSystem.GridToWorldCentered(coordinates)
            + new Vector3(0f, 0.05f, 0f);
    }

    // one speed per crossing, written into every flight corner: DESIGN.md#travel-timed-crossings
    internal virtual float TransitSpeed(Vector3Int entry, Vector3Int exit)
    {
        return 0f;
    }

    // DIRECTIONAL link resolution from a position near one plate (entry = the near side): DESIGN.md#travel-timed-crossings
    internal virtual bool TryGetTimedLink(Vector3 world, out Vector3Int entry, out Vector3Int exit)
    {
        entry = default;
        exit = default;
        return false;
    }

    // any live partner of a station cell, if one exists
    internal bool TryGetFirstSibling(Vector3Int station, out Vector3Int sibling)
    {
        if (_siblings.TryGetValue(station, out var set))
        {
            foreach (var candidate in set)
            {
                sibling = candidate;
                return true;
            }
        }
        sibling = default;
        return false;
    }

    // live link count (one pair = one link)
    internal int LinkCount => _pairs.Count;

    // load-window flag: DESIGN.md#load-window
    internal bool LoadComplete { get; private protected set; }

    protected TeleporterLinkIndex(INavMeshService navMeshService,
        NavMeshGroupService navMeshGroupService, NodeIdService nodeIdService,
        TerrainNavMeshSource terrainNavMeshSource,
        InstantTerrainNavMeshSource instantTerrainNavMeshSource,
        PreviewTerrainNavMeshSource previewTerrainNavMeshSource,
        RoadNavMeshSource roadNavMeshSource,
        InstantRoadNavMeshSource instantRoadNavMeshSource,
        PreviewRoadNavMeshSource previewRoadNavMeshSource)
    {
        _navMeshService = navMeshService;
        _navMeshGroupService = navMeshGroupService;
        _nodeIdService = nodeIdService;
        _terrainNavMeshSource = terrainNavMeshSource;
        _instantTerrainNavMeshSource = instantTerrainNavMeshSource;
        _previewTerrainNavMeshSource = previewTerrainNavMeshSource;
        _roadNavMeshSource = roadNavMeshSource;
        _instantRoadNavMeshSource = instantRoadNavMeshSource;
        _previewRoadNavMeshSource = previewRoadNavMeshSource;
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
            RemoveEdge(pair.Item1, pair.Item2);
            RemoveEdge(pair.Item2, pair.Item1);
            RemoveChain(pair.Item1, pair.Item2);
            _pairs.Remove(pair);
            removed++;
        }
        var added = 0;
        foreach (var pair in desired)
        {
            if (_pairs.Contains(pair))
            {
                continue;
            }
            AddEdge(pair.Item1, pair.Item2);
            AddEdge(pair.Item2, pair.Item1);
            AddChain(pair.Item1, pair.Item2);
            _pairs.Add(pair);
            added++;
        }
        if (added > 0 || removed > 0)
        {
            RebuildSiblingIndex();
        }
        return (added, removed);
    }

    // the tubeway's rule, the zipline's shape: mid-air cells carry connected walk nodes (the district test flood-fills terrain, never roads), at ten times ground cost so the terrain-fallback pathfinder never prefers the span: DESIGN.md#travel-timed-crossings
    private const float ChainUnitCost = 10f;

    private void AddChain(Vector3Int start, Vector3Int end)
    {
        var cells = ChainCells(start, end);
        for (var i = 0; i < cells.Count - 1; i++)
        {
            WriteChainEdge(cells[i], cells[i + 1], add: true);
        }
    }

    private void RemoveChain(Vector3Int start, Vector3Int end)
    {
        var cells = ChainCells(start, end);
        for (var i = 0; i < cells.Count - 1; i++)
        {
            WriteChainEdge(cells[i], cells[i + 1], add: false);
        }
    }

    // Amanatides-Woo voxel walk; one axis per step, exact integer fraction comparison
    private static List<Vector3Int> ChainCells(Vector3Int start, Vector3Int end)
    {
        var (a, b) = Canonical(start, end);
        var cells = new List<Vector3Int> { a };
        var current = a;
        var dx = Math.Abs(b.x - a.x);
        var dy = Math.Abs(b.y - a.y);
        var dz = Math.Abs(b.z - a.z);
        var sx = Math.Sign(b.x - a.x);
        var sy = Math.Sign(b.y - a.y);
        var sz = Math.Sign(b.z - a.z);
        // the next boundary crossing per axis sits at fraction crossed/total
        var crossedX = 1;
        var crossedY = 1;
        var crossedZ = 1;
        while (current != b)
        {
            var xNext = dx > 0 && (dy == 0 || crossedX * dy <= crossedY * dx)
                      && (dz == 0 || crossedX * dz <= crossedZ * dx);
            var yNext = !xNext && dy > 0 && (dz == 0 || crossedY * dz <= crossedZ * dy);
            if (xNext)
            {
                current += new Vector3Int(sx, 0, 0);
                crossedX++;
            }
            else if (yNext)
            {
                current += new Vector3Int(0, sy, 0);
                crossedY++;
            }
            else
            {
                current += new Vector3Int(0, 0, sz);
                crossedZ++;
            }
            cells.Add(current);
        }
        return cells;
    }

    // terrain graphs only (isRoad false keeps routing in the road maps), both directions, direct during load; refcounted because two pairs can share a voxel step: DESIGN.md#load-window
    private void WriteChainEdge(Vector3Int from, Vector3Int to, bool add)
    {
        WriteChainEdge(from, to, from, to, add);
        WriteChainEdge(from, to, to, from, add);
    }

    private void WriteChainEdge(Vector3Int keyA, Vector3Int keyB, Vector3Int edgeFrom,
        Vector3Int edgeTo, bool add)
    {
        var key = (keyA, keyB);
        if (add)
        {
            if (_chainEdges.TryGetValue(key, out var held))
            {
                _chainEdges[key] = (held.Edge, held.Sharers + 1);
                return;
            }
            var edge = NavMeshEdge.CreateGrouped(edgeFrom, edgeTo, GroupId, isRoad: false,
                ChainUnitCost);
            _chainEdges.Add(key, (edge, 1));
            if (LoadComplete)
            {
                _navMeshService.AddEdge(edge);
            }
            else
            {
                WriteChainDirect(edgeFrom, edgeTo, edge, add: true);
            }
        }
        else
        {
            if (!_chainEdges.TryGetValue(key, out var held))
            {
                return;
            }
            if (held.Sharers > 1)
            {
                _chainEdges[key] = (held.Edge, held.Sharers - 1);
                return;
            }
            _chainEdges.Remove(key);
            if (LoadComplete)
            {
                _navMeshService.RemoveEdge(held.Edge);
            }
            else
            {
                WriteChainDirect(edgeFrom, edgeTo, held.Edge, add: false);
            }
        }
    }

    private void WriteChainDirect(Vector3Int edgeFrom, Vector3Int edgeTo, NavMeshEdge edge,
        bool add)
    {
        var startId = _nodeIdService.GridToId(edgeFrom);
        var endId = _nodeIdService.GridToId(edgeTo);
        if (add)
        {
            _terrainNavMeshSource.AddEdge(startId, endId, edge.GroupId, edge.Cost);
            _instantTerrainNavMeshSource.AddEdge(startId, endId, edge.GroupId, edge.Cost);
        }
        else
        {
            _terrainNavMeshSource.RemoveEdge(startId, endId, edge.GroupId, edge.Cost);
            _instantTerrainNavMeshSource.RemoveEdge(startId, endId, edge.GroupId, edge.Cost);
        }
    }

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
        var edge = NavMeshEdge.CreateGrouped(a, b, GroupId, isRoad: true, 0f);
        if (add)
        {
            _navMeshService.AddPreviewEdge(edge);
            _navMeshService.AddPreviewEdge(
                NavMeshEdge.CreateGrouped(b, a, GroupId, isRoad: true, 0f));
        }
        else
        {
            _navMeshService.RemovePreviewEdge(edge);
            _navMeshService.RemovePreviewEdge(
                NavMeshEdge.CreateGrouped(b, a, GroupId, isRoad: true, 0f));
        }
    }

    // geometric crossing match via sibling index: DESIGN.md#crossing-detection-and-billing
    internal bool MatchLiveLink(Vector3 fromWorld, Vector3 toWorld, out Vector3Int entry, out Vector3Int exit)
    {
        entry = default;
        exit = default;
        if (_pairs.Count == 0)
        {
            return false;
        }
        var fromCell = NavigationCoordinateSystem.WorldToGridInt(fromWorld);
        var toCell = NavigationCoordinateSystem.WorldToGridInt(toWorld);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var a = new Vector3Int(fromCell.x + dx, fromCell.y + dy, fromCell.z + dz);
            if (!_siblings.TryGetValue(a, out var siblings)
                || !Near(fromWorld, NavigationCoordinateSystem.GridToWorld(a)))
            {
                continue;
            }
            for (var ex = -1; ex <= 1; ex++)
            for (var ey = -1; ey <= 1; ey++)
            for (var ez = -1; ez <= 1; ez++)
            {
                var b = new Vector3Int(toCell.x + ex, toCell.y + ey, toCell.z + ez);
                if (siblings.Contains(b)
                    && Near(toWorld, NavigationCoordinateSystem.GridToWorld(b)))
                {
                    entry = a;
                    exit = b;
                    return true;
                }
            }
        }
        return false;
    }

    private static bool Near(Vector3 a, Vector3 b)
    {
        return Vector3.Distance(a, b) < 0.75f;
    }

    // ARRIVAL-side lookup: DESIGN.md#crossing-detection-and-billing
    internal bool TryGetStationAt(Vector3 world, out Vector3Int station)
    {
        return TryGetStationNear(world, 0.35f, out station);
    }

    // DEPARTURE-side wider window: DESIGN.md#crossing-detection-and-billing
    internal bool TryGetStationNear(Vector3 world, float maxDistance, out Vector3Int station)
    {
        station = default;
        var cell = NavigationCoordinateSystem.WorldToGridInt(world);
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        for (var dz = -1; dz <= 1; dz++)
        {
            var a = new Vector3Int(cell.x + dx, cell.y + dy, cell.z + dz);
            if (_siblings.ContainsKey(a)
                && Vector3.Distance(world, NavigationCoordinateSystem.GridToWorld(a)) < maxDistance)
            {
                station = a;
                return true;
            }
        }
        return false;
    }

    internal bool IsLinked(Vector3Int a, Vector3Int b)
    {
        return _siblings.TryGetValue(a, out var set) && set.Contains(b);
    }

    internal Vector3Int? NearestSibling(Vector3Int exit, Vector3 fromWorld, float maxDistance)
    {
        if (!_siblings.TryGetValue(exit, out var siblings))
        {
            return null;
        }
        Vector3Int? best = null;
        var bestDistance = float.MaxValue;
        foreach (var sibling in siblings)
        {
            var distance = Vector3.Distance(fromWorld, NavigationCoordinateSystem.GridToWorld(sibling));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = sibling;
            }
        }
        return bestDistance <= maxDistance ? best : null;
    }

    // the tracker bills the system that owns the entry cell
    internal abstract void BillCrossing(Vector3Int entry);

    private void AddEdge(Vector3Int start, Vector3Int end)
    {
        var edge = NavMeshEdge.CreateGrouped(start, end, GroupId, isRoad: true, EdgeCost(start, end));
        if (!_edges.TryAdd((start, end), edge))
        {
            return;
        }
        if (LoadComplete)
        {
            _navMeshService.AddEdge(edge);
        }
        else
        {
            WriteDirectly(start, end, edge, add: true);
        }
    }

    private void RemoveEdge(Vector3Int start, Vector3Int end)
    {
        if (!_edges.Remove((start, end), out var edge))
        {
            return;
        }
        if (LoadComplete)
        {
            _navMeshService.RemoveEdge(edge);
        }
        else
        {
            WriteDirectly(start, end, edge, add: false);
        }
    }

    // load-window direct writes into all six graphs: DESIGN.md#load-window
    private void WriteDirectly(Vector3Int start, Vector3Int end, NavMeshEdge edge, bool add)
    {
        var startId = _nodeIdService.GridToId(start);
        var endId = _nodeIdService.GridToId(end);
        if (add)
        {
            _terrainNavMeshSource.AddEdge(startId, endId, edge.GroupId, edge.Cost);
            _instantTerrainNavMeshSource.AddEdge(startId, endId, edge.GroupId, edge.Cost);
            _roadNavMeshSource.AddEdge(startId, endId, edge.GroupId, edge.Cost);
            _instantRoadNavMeshSource.AddEdge(startId, endId, edge.GroupId, edge.Cost);
            _previewTerrainNavMeshSource.AddEdge(startId, endId, edge.GroupId, edge.Cost);
            _previewRoadNavMeshSource.AddEdge(startId, endId, edge.GroupId, edge.Cost);
        }
        else
        {
            _terrainNavMeshSource.RemoveEdge(startId, endId, edge.GroupId, edge.Cost);
            _instantTerrainNavMeshSource.RemoveEdge(startId, endId, edge.GroupId, edge.Cost);
            _roadNavMeshSource.RemoveEdge(startId, endId, edge.GroupId, edge.Cost);
            _instantRoadNavMeshSource.RemoveEdge(startId, endId, edge.GroupId, edge.Cost);
            _previewTerrainNavMeshSource.RemoveEdge(startId, endId, edge.GroupId, edge.Cost);
            _previewRoadNavMeshSource.RemoveEdge(startId, endId, edge.GroupId, edge.Cost);
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
