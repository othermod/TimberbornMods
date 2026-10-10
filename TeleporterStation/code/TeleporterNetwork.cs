using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.Characters;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Navigation;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace TeleporterStation;

// design: DESIGN.md#one-network, DESIGN.md#path-reachability
public class TeleporterNetwork : ILoadableSingleton, IPostLoadableSingleton,
    IPrioritizedSingletonInstantNavMeshListener
{
    private const string GroupName = "Teleporter";

    private readonly INavMeshService _navMeshService;
    private readonly NavMeshGroupService _navMeshGroupService;
    private readonly DistrictCenterRegistry _districtCenterRegistry;
    private readonly NodeIdService _nodeIdService;
    private readonly InstantRoadNavMeshSource _instantRoadNavMeshSource;
    private readonly RoadNavMeshSource _roadNavMeshSource;
    private readonly PreviewRoadNavMeshSource _previewRoadNavMeshSource;
    private readonly InstantRoadNavMeshGraph _instantRoadNavMeshGraph;
    private readonly TerrainNavMeshSource _terrainNavMeshSource;
    private readonly InstantTerrainNavMeshSource _instantTerrainNavMeshSource;
    private readonly PreviewTerrainNavMeshSource _previewTerrainNavMeshSource;
    private readonly EntityRegistry _entityRegistry;
    private readonly EventBus _eventBus;

    private readonly HashSet<Vector3Int> _nodes = new();
    private readonly HashSet<Vector3Int> _chargedNodes = new();
    private readonly Dictionary<Vector3Int, TeleporterNode> _stations = new();
    private readonly Dictionary<(Vector3Int, Vector3Int), NavMeshEdge> _edges = new();
    private readonly HashSet<(Vector3Int, Vector3Int)> _pairs = new();

    // MatchLiveLink O(1) index: docs/performance.md
    private readonly Dictionary<Vector3Int, HashSet<Vector3Int>> _siblings = new();

    // which district's real paths reach each road node: DESIGN.md#path-reachability
    private Dictionary<int, int> _realClaim = new();

    // the district whose paths reach a station, -1 while none does. All
    // teleporters live on this one district (the placement veto enforces
    // it); wilderness stations belong to it through the wormhole.
    private int _networkDistrict = -1;

    private readonly Dictionary<Vector3Int, int> _pairedVisuals = new();
    private readonly HashSet<(int, int)> _reportedConflicts = new();
    private bool _conflicted;

    // a network is LIVE while some path-connected member holds extract:
    // the road-side door beavers can actually walk through. Dark means no
    // links anywhere, dark horizons, and the offline status owns the
    // explanation: DESIGN.md#network-liveness
    private bool _live;

    private int _groupId;

    // load-window flag: DESIGN.md#load-window
    private bool _loadComplete;

    // payload carries the host district's center node id: DESIGN.md#one-network
    internal event System.Action<Vector3Int, int> PairingChanged;

    // fires after the refresh: DESIGN.md#one-network
    internal event System.Action<Vector3Int> StationChargeChanged;

    // fires after the refresh: DESIGN.md#network-liveness
    internal event System.Action NetworkLivenessChanged;

    internal bool IsNetworkLive()
    {
        return _live;
    }

    internal int TeleporterGroupId => _groupId;

    // every station of the one network: DESIGN.md#one-network
    internal IEnumerable<TeleporterNode> NetworkMembers()
    {
        if (_networkDistrict == -1)
        {
            yield break;
        }
        foreach (var entry in _stations)
        {
            if (BelongsToNetwork(entry.Key))
            {
                yield return entry.Value;
            }
        }
    }

    // a station belongs when its tile is the host district's road, or no
    // road reaches it at all (wilderness: it joins through the wormhole).
    // A tile held by a FOREIGN district (a pre-rule save that pathed a
    // second district to its teleporters) stays an inert ring.
    private bool BelongsToNetwork(Vector3Int coordinates)
    {
        if (_networkDistrict == -1)
        {
            return false;
        }
        var claim = _realClaim.GetValueOrDefault(_nodeIdService.GridToId(coordinates), -1);
        return claim == -1 || claim == _networkDistrict;
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

    public TeleporterNetwork(INavMeshService navMeshService, NavMeshGroupService navMeshGroupService,
        DistrictCenterRegistry districtCenterRegistry, NodeIdService nodeIdService,
        InstantRoadNavMeshSource instantRoadNavMeshSource, RoadNavMeshSource roadNavMeshSource,
        PreviewRoadNavMeshSource previewRoadNavMeshSource,
        InstantRoadNavMeshGraph instantRoadNavMeshGraph, TerrainNavMeshSource terrainNavMeshSource,
        InstantTerrainNavMeshSource instantTerrainNavMeshSource,
        PreviewTerrainNavMeshSource previewTerrainNavMeshSource, EntityRegistry entityRegistry,
        EventBus eventBus)
    {
        _navMeshService = navMeshService;
        _navMeshGroupService = navMeshGroupService;
        _districtCenterRegistry = districtCenterRegistry;
        _nodeIdService = nodeIdService;
        _instantRoadNavMeshSource = instantRoadNavMeshSource;
        _roadNavMeshSource = roadNavMeshSource;
        _previewRoadNavMeshSource = previewRoadNavMeshSource;
        _instantRoadNavMeshGraph = instantRoadNavMeshGraph;
        _terrainNavMeshSource = terrainNavMeshSource;
        _instantTerrainNavMeshSource = instantTerrainNavMeshSource;
        _previewTerrainNavMeshSource = previewTerrainNavMeshSource;
        _entityRegistry = entityRegistry;
        _eventBus = eventBus;
    }

    public void Load()
    {
        _groupId = _navMeshGroupService.GetOrAddGroupId(GroupName);
        _loadComplete = false;
        _eventBus.Register(this);
        Debug.Log($"[TeleporterStation] nav mesh group '{GroupName}' = {_groupId}");
    }

    // the load-window boundary: DESIGN.md#load-window
    public void PostLoad()
    {
        _loadComplete = true;
        // the load-window refreshes settled the conflict and liveness
        // flags silently, one registration at a time; reset them so the
        // load-time refresh always narrates the verdict
        _conflicted = false;
        _live = false;
        RefreshLinks();
        SafeRestoreStrandedDistricts();
        PulseDistrictAssignment();
        var uncharged = _nodes.Count - _chargedNodes.Count;
        Debug.Log($"[TeleporterStation] network ready: {Describe(_networkDistrict)}, " +
            $"{_nodes.Count} station(s), {_pairs.Count} link(s)" +
            (uncharged > 0 ? $", {uncharged} out of extract" : string.Empty));
    }

    [OnEvent]
    // a district center appearing or disappearing changes what the real
    // paths reach: DESIGN.md#path-reachability
    public void OnDistrictCenterRegistryChanged(DistrictCenterRegistryChangedEvent _)
    {
        RefreshLinks();
    }

    public void Register(Vector3Int coordinates, bool charged = true)
    {
        if (charged)
        {
            _chargedNodes.Add(coordinates);
        }
        else
        {
            _chargedNodes.Remove(coordinates);
        }
        if (!_nodes.Add(coordinates))
        {
            return;
        }
        RefreshLinks();
        ScanAutoRefills();
    }

    public void RegisterStation(Vector3Int coordinates, TeleporterNode station)
    {
        _stations[coordinates] = station;
    }

    public void Unregister(Vector3Int coordinates)
    {
        _stations.Remove(coordinates);
        if (_nodes.Remove(coordinates))
        {
            _chargedNodes.Remove(coordinates);
            _pairedVisuals.Remove(coordinates);
            RefreshLinks();
        }
    }

    public void SetStationCharged(Vector3Int coordinates, bool charged)
    {
        if (!_nodes.Contains(coordinates))
        {
            return;
        }
        var changed = charged ? _chargedNodes.Add(coordinates) : _chargedNodes.Remove(coordinates);
        if (!changed)
        {
            return;
        }
        Debug.Log($"[TeleporterStation] {coordinates} " +
                      (charged ? "charged, extract in store" : "out of extract, going offline"));
        RefreshLinks();
        StationChargeChanged?.Invoke(coordinates);
    }

    // DESIGN.md#crossing-detection-and-billing
    public void BillCrossing(Vector3Int entry)
    {
        _stations.GetValueOrDefault(entry)?.RecordCrossing();
    }

    // rescue, not equalizer: DESIGN.md#wormhole-siphon
    internal const int RelayBatch = 5;
    internal const int RelayDonorFloor = 20;
    internal const int AutoRefillThreshold = 5;

    private bool _scanningAutoRefills;

    // donors must be network members AND path-connected: a foreign
    // district's station (a pre-rule save) is path-connected by its OWN
    // district's paths, and a wilderness member's extract rode the
    // wormhole; neither is part of the reserve (DESIGN.md#wormhole-siphon)
    private (Vector3Int Coords, TeleporterNode Node)? FindFuelDonor(Vector3Int requester)
    {
        if (_networkDistrict == -1)
        {
            return null;
        }
        (Vector3Int, TeleporterNode)? best = null;
        var bestStock = 0;
        foreach (var (coordinates, station) in _stations)
        {
            if (coordinates == requester
                || !BelongsToNetwork(coordinates)
                || !station.IsExtractSource
                || !IsReachableByRealPath(coordinates))
            {
                continue;
            }
            var stock = station.UnreservedCharges;
            if (stock > RelayDonorFloor && (best == null || stock > bestStock))
            {
                best = (coordinates, station);
                bestStock = stock;
            }
        }
        return best;
    }

    internal bool HasDesignatedSource(Vector3Int requester)
    {
        if (_networkDistrict == -1)
        {
            return false;
        }
        foreach (var (coordinates, station) in _stations)
        {
            if (coordinates != requester
                && station.IsExtractSource
                && BelongsToNetwork(coordinates))
            {
                return true;
            }
        }
        return false;
    }

    // DESIGN.md#wormhole-siphon
    internal void ScanAutoRefills()
    {
        if (!_loadComplete || _scanningAutoRefills)
        {
            return;
        }
        _scanningAutoRefills = true;
        try
        {
            foreach (var (coordinates, station) in _stations)
            {
                if (!BelongsToNetwork(coordinates)
                    || station.Charges > AutoRefillThreshold)
                {
                    continue;
                }
                var donor = FindFuelDonor(coordinates);
                if (donor == null)
                {
                    continue;
                }
                var amount = Math.Min(RelayBatch,
                    donor.Value.Node.UnreservedCharges - RelayDonorFloor);
                donor.Value.Node.RelayOut(amount);
                station.RelayIn(amount);
                Debug.Log($"[TeleporterStation] auto refill: {amount} extract " +
                          $"{donor.Value.Coords} -> {coordinates}");
            }
        }
        finally
        {
            _scanningAutoRefills = false;
        }
    }

    // the one network's district, or null while no district's paths reach
    // any station: DESIGN.md#one-network
    internal DistrictCenter NetworkDistrict()
    {
        return _networkDistrict == -1 ? null : FindCenterByNodeId(_networkDistrict);
    }

    // teleporters stand on more than one district (a pre-0.2 save): every
    // network is offline until the player demolishes one side whole
    internal bool IsConflicted => _conflicted;

    public bool IsReachableByRealPath(Vector3Int coordinates)
    {
        return _realClaim.ContainsKey(_nodeIdService.GridToId(coordinates));
    }

    // only a network member on real paths can serve the siphon: a foreign
    // district's station (pre-rule save) is reachable by its OWN paths
    // but is not part of this network
    internal bool CanServeAsSource(Vector3Int coordinates)
    {
        return BelongsToNetwork(coordinates) && IsReachableByRealPath(coordinates);
    }

    // instant-only: docs/performance.md
    public void OnInstantNavMeshUpdated(NavMeshUpdate navMeshUpdate)
    {
        if (navMeshUpdate.UpdatedRoads)
        {
            RefreshLinks();
        }
    }

    // one BFS from every finished district center at once, first-wins
    // labels, over real roads ONLY: the walk skips the teleporter group at
    // every hop, so the answer is structurally immune to the mod's own
    // edges (why not the game's own district maps:
    // DESIGN.md#path-reachability)
    private Dictionary<int, int> BuildClaimMap()
    {
        var claims = new Dictionary<int, int>();
        var queue = new Queue<int>();
        foreach (var center in _districtCenterRegistry.FinishedDistrictCenters)
        {
            if (center.District == null)
            {
                continue;
            }
            var id = center.District.CenterNodeId;
            if (claims.TryAdd(id, id))
            {
                queue.Enqueue(id);
            }
        }
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var neighbor in _instantRoadNavMeshGraph.GetNeighbors(current))
            {
                if (neighbor.GroupId == _groupId)
                {
                    continue;
                }
                SpreadClaim(claims, queue, neighbor.Id, claims[current]);
            }
        }
        return claims;
    }

    private void SpreadClaim(Dictionary<int, int> claims, Queue<int> queue, int nodeId, int spreaderId)
    {
        if (claims.TryAdd(nodeId, spreaderId))
        {
            queue.Enqueue(nodeId);
            return;
        }
        var holder = claims[nodeId];
        if (holder == spreaderId)
        {
            return;
        }
        // unreachable while the game's own two-district rules hold; a
        // marker that something already broke the world
        if (_reportedConflicts.Add((holder, spreaderId)))
        {
            Debug.LogWarning($"[TeleporterStation] districts {holder} and {spreaderId} " +
                $"both reach road node {nodeId} through real roads");
        }
    }

    // the refresh: the reachability map, the host district, then the edge
    // diff (all charged members of the network, all pairs)
    private void RefreshLinks()
    {
        _realClaim = BuildClaimMap();

        // the host is the SOLE district whose paths reach a station.
        // Teleporters on two or more districts is a conflict (only a save
        // from a multi-district version can make one): every network
        // stays offline until the player has demolished one district's
        // teleporters whole, and whatever remains then joins the
        // survivor's network on its own (DESIGN.md#one-network)
        var first = -1;
        var conflicted = false;
        foreach (var coordinates in _nodes)
        {
            var claim = _realClaim.GetValueOrDefault(_nodeIdService.GridToId(coordinates), -1);
            if (claim == -1)
            {
                continue;
            }
            if (first == -1)
            {
                first = claim;
            }
            else if (first != claim)
            {
                conflicted = true;
            }
        }
        var host = conflicted ? -1 : first;
        if (conflicted != _conflicted && _loadComplete)
        {
            Debug.Log(conflicted
                ? "[TeleporterStation] teleporters stand on more than one district " +
                  "(a save from a multi-district version); every network stays " +
                  "offline until one district has no teleporters left"
                : $"[TeleporterStation] sole teleporter district: {Describe(host)}");
        }
        if (host != _networkDistrict && _loadComplete)
        {
            Debug.Log($"[TeleporterStation] network district: {Describe(host)} " +
                      $"(was {Describe(_networkDistrict)})");
        }
        _conflicted = conflicted;
        _networkDistrict = host;

        // live: some path-connected member holds extract. A dark network
        // forms no links at all: with the door member dry nobody can walk
        // in, so remote-to-remote wormholes would be an unreachable
        // island glowing as if it worked
        var live = host != -1
            && _chargedNodes.Any(c => _realClaim.ContainsKey(_nodeIdService.GridToId(c)));
        var livenessChanged = live != _live;
        if (_loadComplete && livenessChanged)
        {
            Debug.Log(live
                ? $"[TeleporterStation] network live ({_chargedNodes.Count} charged of " +
                  $"{_nodes.Count} station(s))"
                : "[TeleporterStation] network offline: no path-connected " +
                  "teleporter is charged");
        }
        _live = live;

        var desired = new HashSet<(Vector3Int, Vector3Int)>();
        if (host != -1 && _live)
        {
            foreach (var coordinates in _chargedNodes)
            {
                if (!BelongsToNetwork(coordinates))
                {
                    continue;
                }
                foreach (var other in _chargedNodes)
                {
                    if (other != coordinates && BelongsToNetwork(other))
                    {
                        desired.Add(Canonical(coordinates, other));
                    }
                }
            }
        }

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
            _pairs.Add(pair);
            added++;
        }
        if (added > 0 || removed > 0)
        {
            RebuildSiblingIndex();
        }
        SyncPairedVisuals();
        if (livenessChanged)
        {
            NetworkLivenessChanged?.Invoke();
        }
        if (_loadComplete && (added > 0 || removed > 0))
        {
            Debug.Log($"[TeleporterStation] {desired.Count} link(s) (+{added} / -{removed})");
        }
    }

    // fires PairingChanged only where a station's answer changed
    private void SyncPairedVisuals()
    {
        foreach (var coordinates in _nodes)
        {
            var nodeId = BelongsToNetwork(coordinates) ? _networkDistrict : -1;
            var known = _pairedVisuals.TryGetValue(coordinates, out var seen) ? seen : (int?)null;
            if (known == nodeId)
            {
                continue;
            }
            _pairedVisuals[coordinates] = nodeId;
            if (_loadComplete)
            {
                Debug.Log($"[TeleporterStation] pairing of {coordinates}: " +
                          $"{(known == null ? "new" : Describe(known.Value))} -> {Describe(nodeId)}");
            }
            PairingChanged?.Invoke(coordinates, nodeId);
        }
    }

    private DistrictCenter FindCenterByNodeId(int centerNodeId)
    {
        foreach (var center in _districtCenterRegistry.FinishedDistrictCenters)
        {
            if (center.District != null && center.District.CenterNodeId == centerNodeId)
            {
                return center;
            }
        }
        return null;
    }

    private static string Describe(int centerNodeId)
    {
        return centerNodeId == -1 ? "none" : $"district #{centerNodeId}";
    }

    private static (Vector3Int, Vector3Int) Canonical(Vector3Int a, Vector3Int b)
    {
        return (a.x, a.y, a.z).CompareTo((b.x, b.y, b.z)) <= 0 ? (a, b) : (b, a);
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

    private void AddEdge(Vector3Int start, Vector3Int end)
    {
        var edge = NavMeshEdge.CreateGrouped(start, end, _groupId, isRoad: true, 0f);
        if (!_edges.TryAdd((start, end), edge))
        {
            return;
        }
        if (_loadComplete)
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
        if (_loadComplete)
        {
            _navMeshService.RemoveEdge(edge);
        }
        else
        {
            WriteDirectly(start, end, edge, add: false);
        }
    }

    // load-window direct writes: DESIGN.md#load-window. Nav edge changes
    // via INavMeshService are QUEUED and land only when the nav pipeline
    // drains, after every character's post-load re-path has already run;
    // until PostLoad flips the flag, edges go straight into all six
    // source graphs (terrain and road, instant and regular and preview),
    // so links are complete and live the moment stations register and no
    // graph is left blind to them. Mechanics:
    // docs/game-internals.md, "Load lifecycle is a stranding trap".
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

    // the load window writes edges without firing the game's nav update
    // events, and the district assignment chain only refreshes on those
    // events: DistrictMap.OnNavMeshUpdated re-dirties a district's cached
    // road flow field only for changed nodes the map already attributes
    // to that district, and DistrictBuildingAssigner reassigns only on
    // update events (verified in decomp 2026-10-09). A station connected
    // purely through wormhole edges could therefore stay unassigned for
    // the whole session and show the vanilla unconnected icon. This
    // pulse queues one throwaway road edge between two already-adjacent
    // cells of a path-connected station and removes it again: a real
    // NavMeshUpdate through the game's own pipeline, touching a node the
    // district already claims, which dirties the cached field and
    // triggers a full reassignment.
    private void PulseDistrictAssignment()
    {
        foreach (var coordinates in _nodes)
        {
            if (!_realClaim.ContainsKey(_nodeIdService.GridToId(coordinates)))
            {
                continue;
            }
            foreach (var neighbor in _instantRoadNavMeshGraph
                         .GetNeighbors(_nodeIdService.GridToId(coordinates)))
            {
                if (neighbor.GroupId == _groupId)
                {
                    continue;
                }
                var edge = NavMeshEdge.CreateGrouped(coordinates,
                    _nodeIdService.IdToGrid(neighbor.Id), _groupId, isRoad: true, 0f);
                _navMeshService.AddEdge(edge);
                _navMeshService.RemoveEdge(edge);
                Debug.Log("[TeleporterStation] district assignment pulse: " +
                          $"{coordinates} <-> {_nodeIdService.IdToGrid(neighbor.Id)}");
                return;
            }
        }
    }

    // repairs characters a PREVIOUS version stranded (the game leaves
    // them with no district at all): a district-less citizen standing on
    // a tile the real paths claim gets that district; one standing
    // elsewhere on the one network's territory (a wilderness reached only
    // through the links) gets the host district. A mis-assignment
    // self-corrects: the game strips a district on the next failed path.
    private void SafeRestoreStrandedDistricts()
    {
        try
        {
            var restored = 0;
            foreach (var entity in _entityRegistry.Entities)
            {
                var selectable = entity.GetComponent<SelectableObject>();
                if (selectable == null)
                {
                    continue;
                }
                var character = selectable.GetComponent<Character>();
                if (character == null || character.GameObject == null)
                {
                    continue;
                }
                var citizen = selectable.GetComponent<Citizen>();
                if (citizen == null || citizen.AssignedDistrict != null)
                {
                    continue;
                }
                var cell = NavigationCoordinateSystem.WorldToGridInt(character.Transform.position);
                var claim = _realClaim.GetValueOrDefault(_nodeIdService.GridToId(cell), -1);
                var center = claim != -1 ? FindCenterByNodeId(claim) : FindCenterByNodeId(_networkDistrict);
                if (center == null)
                {
                    continue;
                }
                citizen.AssignDistrict(center);
                Debug.Log($"[TeleporterStation] restored district of {character.GameObject.name} " +
                          $"to {center.DistrictName} (was NONE) at {character.Transform.position}");
                restored++;
            }
            if (restored > 0)
            {
                Debug.Log($"[TeleporterStation] district restore: {restored} character(s)");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[TeleporterStation] district restore failed: {e}");
        }
    }
}
