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

// design: DESIGN.md#one-network, DESIGN.md#path-reachability; edge layer: TeleporterLinkIndex
public class TeleporterNetwork : TeleporterLinkIndex, ILoadableSingleton, IPostLoadableSingleton,
    IPrioritizedSingletonInstantNavMeshListener
{
    private const string GroupLiteral = "Teleporter";

    // crossings take constant TIME, not speed: the tick interval is fixed, so ticks-per-crossing is this constant regardless of game speed or span length: DESIGN.md#travel-timed-crossings
    internal const float CrossingSeconds = 0.45f;

    private readonly DistrictCenterRegistry _districtCenterRegistry;
    private readonly EntityRegistry _entityRegistry;
    private readonly EventBus _eventBus;
    private readonly InstantRoadNavMeshGraph _instantRoadNavMeshGraph;

    private readonly HashSet<Vector3Int> _nodes = new();
    private readonly HashSet<Vector3Int> _chargedNodes = new();
    // stations the vanilla building pause took offline: DESIGN.md#pause
    private readonly HashSet<Vector3Int> _pausedNodes = new();
    private readonly Dictionary<Vector3Int, TeleporterNode> _stations = new();

    // wormhole groups the claim map must never read as real paths
    private readonly HashSet<int> _skippedNavGroups = new();

    // which district's real paths reach each road node: DESIGN.md#path-reachability
    private Dictionary<int, int> _realClaim = new();

    // where a beaver can walk, station wormholes included; built only while rings exist: DESIGN.md#ring-pairs
    private Dictionary<int, int> _walkClaim = new();

    // set by the ring system while any ring is registered: DESIGN.md#ring-pairs
    private bool _ringsExist;

    // the one network's host district, -1 while none: DESIGN.md#one-network
    private int _networkDistrict = -1;

    private readonly Dictionary<Vector3Int, int> _pairedVisuals = new();
    private readonly HashSet<(int, int)> _reportedConflicts = new();
    private bool _conflicted;

    // live while a path-connected member is charged and unpaused: DESIGN.md#network-liveness
    private bool _live;

    // set before the post-load refresh; consumers may close their load windows after it
    private bool _claimsSettled;

    // payload carries the host district's center node id: DESIGN.md#one-network
    internal event System.Action<Vector3Int, int> PairingChanged;

    // fires after the refresh: DESIGN.md#one-network
    internal event System.Action<Vector3Int> StationChargeChanged;

    // fires after the refresh: DESIGN.md#network-liveness
    internal event System.Action NetworkLivenessChanged;

    // fires after the refresh: DESIGN.md#pause
    internal event System.Action<Vector3Int> StationPauseChanged;

    // fires at the end of every RefreshLinks, claim map already committed: DESIGN.md#ring-pairs
    internal event System.Action ClaimMapRefreshed;

    // true from the start of this network's PostLoad on
    internal bool ClaimsSettled => _claimsSettled;

    internal bool IsNetworkLive()
    {
        return _live;
    }

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

    // host road or unclaimed wilderness belongs; a foreign district's tile is inert: DESIGN.md#one-network
    private bool BelongsToNetwork(Vector3Int coordinates)
    {
        if (_networkDistrict == -1)
        {
            return false;
        }
        var claim = _realClaim.GetValueOrDefault(_nodeIdService.GridToId(coordinates), -1);
        return claim == -1 || claim == _networkDistrict;
    }

    public TeleporterNetwork(INavMeshService navMeshService, NavMeshGroupService navMeshGroupService,
        DistrictCenterRegistry districtCenterRegistry, NodeIdService nodeIdService,
        InstantRoadNavMeshSource instantRoadNavMeshSource, RoadNavMeshSource roadNavMeshSource,
        PreviewRoadNavMeshSource previewRoadNavMeshSource,
        InstantRoadNavMeshGraph instantRoadNavMeshGraph, TerrainNavMeshSource terrainNavMeshSource,
        InstantTerrainNavMeshSource instantTerrainNavMeshSource,
        PreviewTerrainNavMeshSource previewTerrainNavMeshSource, EntityRegistry entityRegistry,
        EventBus eventBus)
        : base(navMeshService, navMeshGroupService, nodeIdService, terrainNavMeshSource,
              instantTerrainNavMeshSource, previewTerrainNavMeshSource, roadNavMeshSource,
              instantRoadNavMeshSource, previewRoadNavMeshSource)
    {
        _districtCenterRegistry = districtCenterRegistry;
        _instantRoadNavMeshGraph = instantRoadNavMeshGraph;
        _entityRegistry = entityRegistry;
        _eventBus = eventBus;
    }

    public void Load()
    {
        ResolveGroup(GroupLiteral);
        _claimsSettled = false;
        _eventBus.Register(this);
        Debug.Log($"[TeleporterStation] nav mesh group '{GroupName}' = {GroupId}");
    }

    // a wormhole group the claim map must never walk: DESIGN.md#ring-pairs
    internal void SkipNavGroupInClaimMap(int groupId)
    {
        _skippedNavGroups.Add(groupId);
    }

    // the walk map costs a second BFS per refresh and only rings read it: DESIGN.md#ring-pairs
    internal void SetRingsExist(bool exists)
    {
        if (_ringsExist == exists)
        {
            return;
        }
        _ringsExist = exists;
        RefreshLinks();
    }

    // the load-window boundary: DESIGN.md#load-window
    public void PostLoad()
    {
        LoadComplete = true;
        _claimsSettled = true;
        // reset so the load-time refresh always narrates the verdict
        _conflicted = false;
        _live = false;
        RefreshLinks();
        SafeRestoreStrandedDistricts();
        PulseDistrictAssignment();
        var uncharged = _nodes.Count - _chargedNodes.Count;
        Debug.Log($"[TeleporterStation] network ready: {Describe(_networkDistrict)}, " +
            $"{_nodes.Count} station(s), {LinkCount} link(s)" +
            (uncharged > 0 ? $", {uncharged} out of extract" : string.Empty));
    }

    [OnEvent]
    // a district center appearing or disappearing changes what the paths reach: DESIGN.md#path-reachability
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
            _pausedNodes.Remove(coordinates);
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

    // the vanilla building pause, adopted: DESIGN.md#pause
    public void SetStationPaused(Vector3Int coordinates, bool paused)
    {
        if (!_nodes.Contains(coordinates))
        {
            return;
        }
        var changed = paused ? _pausedNodes.Add(coordinates) : _pausedNodes.Remove(coordinates);
        if (!changed)
        {
            return;
        }
        RefreshLinks();
        StationPauseChanged?.Invoke(coordinates);
    }

    // DESIGN.md#crossing-detection-and-billing
    internal override void BillCrossing(Vector3Int entry)
    {
        _stations.GetValueOrDefault(entry)?.RecordCrossing();
    }

    // constant TIME per crossing: edge cost is seconds, so speed = span / seconds: DESIGN.md#travel-timed-crossings
    internal override float EdgeCost(Vector3Int start, Vector3Int end)
    {
        return CrossingSeconds;
    }

    // the whole flight takes CrossingSeconds regardless of span: DESIGN.md#travel-timed-crossings
    internal override float TransitSpeed(Vector3Int entry, Vector3Int exit)
    {
        return Vector3.Distance(AnchorWorld(entry), AnchorWorld(exit)) / CrossingSeconds;
    }

    // DIRECTIONAL: the station near the position boards, any live partner is the exit
    internal override bool TryGetTimedLink(Vector3 world, out Vector3Int entry, out Vector3Int exit)
    {
        if (TryGetStationNear(world, 3f, out var station)
            && TryGetFirstSibling(station, out var sibling))
        {
            entry = station;
            exit = sibling;
            return true;
        }
        entry = default;
        exit = default;
        return false;
    }

    // rescue, not equalizer: DESIGN.md#wormhole-siphon
    internal const int RelayBatch = 5;
    internal const int RelayDonorFloor = 20;
    internal const int AutoRefillThreshold = 5;

    private bool _scanningAutoRefills;

    // donors must be network members AND path-connected: DESIGN.md#wormhole-siphon
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
                || station.IsPaused
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
        if (!LoadComplete || _scanningAutoRefills)
        {
            return;
        }
        _scanningAutoRefills = true;
        try
        {
            foreach (var (coordinates, station) in _stations)
            {
                if (!BelongsToNetwork(coordinates)
                    || station.IsPaused
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

    // the one network's district, or null while none: DESIGN.md#one-network
    internal DistrictCenter NetworkDistrict()
    {
        return _networkDistrict == -1 ? null : FindCenterByNodeId(_networkDistrict);
    }

    // teleporters on more than one district (a pre-0.2 save): DESIGN.md#one-network
    internal bool IsConflicted => _conflicted;

    public bool IsReachableByRealPath(Vector3Int coordinates)
    {
        return _realClaim.ContainsKey(_nodeIdService.GridToId(coordinates));
    }

    // the beaver's answer: real paths plus the live network's walkable edges: DESIGN.md#ring-pairs
    internal bool IsReachableByWalk(Vector3Int coordinates)
    {
        return _walkClaim.ContainsKey(_nodeIdService.GridToId(coordinates));
    }

    // only a network member on real paths can serve the siphon: DESIGN.md#wormhole-siphon
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

    // BFS from every finished center; stationEdgesCount lets the walk map cross station edges: DESIGN.md#path-reachability
    private Dictionary<int, int> BuildClaimMap(bool stationEdgesCount)
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
                if (neighbor.GroupId == GroupId && !stationEdgesCount
                    || _skippedNavGroups.Contains(neighbor.GroupId))
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
        // unreachable while the game's two-district rules hold: DESIGN.md#one-network
        if (_reportedConflicts.Add((holder, spreaderId)))
        {
            Debug.LogWarning($"[TeleporterStation] districts {holder} and {spreaderId} " +
                $"both reach road node {nodeId} through real roads");
        }
    }

    // the refresh: claim map, host district, liveness, then the edge diff
    private void RefreshLinks()
    {
        _realClaim = BuildClaimMap(false);
        // the walk map is unread while no rings exist; leaving the stale one is safe: DESIGN.md#ring-pairs
        _walkClaim = _ringsExist ? BuildClaimMap(true) : _walkClaim;

        // the host is the SOLE district whose paths reach a station: DESIGN.md#one-network
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
        if (conflicted != _conflicted && LoadComplete)
        {
            Debug.Log(conflicted
                ? "[TeleporterStation] teleporters stand on more than one district " +
                  "(a save from a multi-district version); every network stays " +
                  "offline until one district has no teleporters left"
                : $"[TeleporterStation] sole teleporter district: {Describe(host)}");
        }
        if (host != _networkDistrict && LoadComplete)
        {
            Debug.Log($"[TeleporterStation] network district: {Describe(host)} " +
                      $"(was {Describe(_networkDistrict)})");
        }
        _conflicted = conflicted;
        _networkDistrict = host;

        // a dark network forms no links at all: DESIGN.md#network-liveness
        var live = host != -1
            && _chargedNodes.Any(c => !_pausedNodes.Contains(c)
                && _realClaim.ContainsKey(_nodeIdService.GridToId(c)));
        var livenessChanged = live != _live;
        if (LoadComplete && livenessChanged)
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
                if (_pausedNodes.Contains(coordinates) || !BelongsToNetwork(coordinates))
                {
                    continue;
                }
                foreach (var other in _chargedNodes)
                {
                    if (other != coordinates && !_pausedNodes.Contains(other)
                        && BelongsToNetwork(other))
                    {
                        desired.Add(Canonical(coordinates, other));
                    }
                }
            }
        }

        var (added, removed) = ApplyLinks(desired);
        SyncPairedVisuals();
        if (livenessChanged)
        {
            NetworkLivenessChanged?.Invoke();
        }
        if (LoadComplete && (added > 0 || removed > 0))
        {
            Debug.Log($"[TeleporterStation] {desired.Count} link(s) (+{added} / -{removed})");
        }
        // subscribers re-read the fresh claim map synchronously: DESIGN.md#ring-pairs
        ClaimMapRefreshed?.Invoke();
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
            if (LoadComplete)
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

    // one queued add/remove edge forces the district reassignment: DESIGN.md#load-window
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
                if (neighbor.GroupId == GroupId || _skippedNavGroups.Contains(neighbor.GroupId))
                {
                    continue;
                }
                var edge = NavMeshEdge.CreateGrouped(coordinates,
                    _nodeIdService.IdToGrid(neighbor.Id), GroupId, isRoad: true, 0f);
                _navMeshService.AddEdge(edge);
                _navMeshService.RemoveEdge(edge);
                Debug.Log("[TeleporterStation] district assignment pulse: " +
                          $"{coordinates} <-> {_nodeIdService.IdToGrid(neighbor.Id)}");
                return;
            }
        }
    }

    // repairs characters a previous version left district-less: DESIGN.md#load-window
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
