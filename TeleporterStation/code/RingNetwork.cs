using System;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BlockSystem;
using Timberborn.Common;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Timberborn.SingletonSystem;
using Timberborn.ZiplineSystem;
using UnityEngine;

namespace TeleporterStation;

// ring pairs, one-to-one routes independent of the station network: DESIGN.md#ring-pairs
public class RingNetwork : TeleporterLinkIndex, ILoadableSingleton, IPostLoadableSingleton
{
    // the group literal ResolveGroup stores into the base's GroupName
    private const string GroupLiteral = "TeleporterRing";

    private readonly TeleporterNetwork _network;
    private readonly EntityService _entityService;
    private readonly ZiplineConnectionService _ziplineConnections;

    private readonly Dictionary<Vector3Int, RingNode> _rings = new();
    private readonly Dictionary<Vector3Int, (bool Paired, bool Live, bool Charged, bool Paused)> _signatures = new();

    // the instant span costs the same as a station flight: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal override float EdgeCost => 0.01f;

    // with the network's settled claims this closes the load window: DESIGN.md#load-window
    private bool _postLoaded;

    // fires at the end of a refresh when a ring's signature changed: DESIGN.md#ring-pairs
    internal event Action<Vector3Int> RingStateChanged;

    public RingNetwork(TeleporterNetwork network, EntityService entityService,
        ZiplineConnectionService ziplineConnections, INavMeshService navMeshService,
        NavMeshGroupService navMeshGroupService, IBlockService blockService,
        BresenhamLineDrawer bresenhamLines)
        : base(navMeshService, navMeshGroupService, blockService, bresenhamLines)
    {
        _network = network;
        _entityService = entityService;
        _ziplineConnections = ziplineConnections;
    }

    public void Load()
    {
        ResolveGroup(GroupLiteral);
        _network.ClaimMapRefreshed += OnClaimMapRefreshed;
        Debug.Log($"[TeleporterStation] ring nav mesh group '{GroupName}' = {GroupId}");
    }

    // the window closes at the later of our PostLoad and the network's settled claims: DESIGN.md#load-window
    public void PostLoad()
    {
        _postLoaded = true;
        RefreshLinks();
        // the game reconnected pairs at load with fresh specs and left occupancy blocks on them: DESIGN.md#ring-pairs
        SweepConnectionBlocks(
            _rings.Values.Select(ring => ring.GetComponent<ZiplineTower>()).Where(t => t != null),
            other => RingCoordsOf(other, out _));
        if (_network.ClaimsSettled)
        {
            LoadComplete = true;
        }
        Debug.Log($"[TeleporterStation] ring network ready: {_rings.Count} ring(s), {LinkCount} route(s)");
    }

    private void OnClaimMapRefreshed()
    {
        RefreshLinks();
        if (_postLoaded && _network.ClaimsSettled)
        {
            LoadComplete = true;
        }
    }

    internal void Register(Vector3Int coordinates, RingNode ring)
    {
        _rings[coordinates] = ring;
        // the walk map is built only while rings exist: DESIGN.md#ring-pairs
        _network.SetRingsExist(true);
        RefreshLinks();
    }

    internal void Unregister(Vector3Int coordinates)
    {
        if (!_rings.Remove(coordinates, out var removed))
        {
            return;
        }
        _signatures.Remove(coordinates);
        RingNode cascade = null;
        // the survivor can pair again from its panel: DESIGN.md#ring-pairs
        foreach (var ring in _rings.Values)
        {
            if (ring.Partner != removed)
            {
                continue;
            }
            ring.ClearPartner();
            // deleting the master takes its remote with it: DESIGN.md#ring-pairs
            if (!removed.IsRemote)
            {
                cascade = ring;
            }
        }
        if (cascade != null)
        {
            Debug.Log($"[TeleporterStation] ring master demolished; " +
                      $"remote {cascade.Coordinates} deleted with it");
            _entityService.Delete(cascade);
        }
        RefreshLinks();
        if (_rings.Count == 0)
        {
            _network.SetRingsExist(false);
        }
    }

    // charge and pause flips are narrated by SyncSignatures; these are the refresh triggers
    internal void SetRingCharged(Vector3Int coordinates, bool charged)
    {
        if (!_rings.ContainsKey(coordinates))
        {
            return;
        }
        RefreshLinks();
    }

    // the vanilla building pause, adopted: DESIGN.md#pause
    internal void SetRingPaused(Vector3Int coordinates, bool paused)
    {
        if (!_rings.ContainsKey(coordinates))
        {
            return;
        }
        RefreshLinks();
    }

    // a node's partner reference changed (linked by the flow, or on load)
    internal void OnPairingChanged()
    {
        RefreshLinks();
    }

    internal RingNode RingAt(Vector3Int coordinates)
    {
        return _rings.GetValueOrDefault(coordinates);
    }

    // the plate's top center, the anchor the span flies from: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal override Vector3 AnchorWorld(Vector3Int coordinates)
    {
        return RingAt(coordinates)?.AnchorWorld()
               ?? CoordinateSystem.GridToWorldCentered(coordinates);
    }

    // the burst flies in the network district's hue: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal Color TravellerColor =>
        TeleporterNode.DistrictColor(_network.NetworkDistrict()?.District.CenterNodeId ?? -1);

    internal bool IsRouteLive(Vector3Int coordinates)
    {
        return HasAnyLink(coordinates);
    }

    // the bill is the crossing's exact instant; both plates burst together in the same call: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal override void BillCrossing(Vector3Int entry)
    {
        var ring = _rings.GetValueOrDefault(entry);
        if (ring == null)
        {
            return;
        }
        ring.RecordCrossing();
        ring.PlayCrossingBurst();
        ring.Partner?.PlayCrossingBurst();
    }

    // activations identify rings by their tower; the scan runs only on those rare events: DESIGN.md#ring-pairs
    internal bool RingCoordsOf(ZiplineTower tower, out Vector3Int coordinates)
    {
        foreach (var (cell, ring) in _rings)
        {
            if (ring.GetComponent<ZiplineTower>() == tower)
            {
                coordinates = cell;
                return true;
            }
        }
        coordinates = default;
        return false;
    }

    // ownership checks route through the same scan: DESIGN.md#ring-pairs
    internal override bool CoordsOf(ZiplineTower tower, out Vector3Int coordinates)
    {
        return RingCoordsOf(tower, out coordinates);
    }

    private bool CanCarve(RingNode ring)
    {
        return ring.IsFinished && !ring.IsPaused && ring.IsCharged;
    }

    // the ring pair is two instant spans, the station's recipe; leftover cables churn out with the pair, never at load: DESIGN.md#travel-every-crossing-is-an-instant-jump
    internal override bool WritePair(Vector3Int start, Vector3Int end, bool add)
    {
        var towerA = _rings.GetValueOrDefault(start)?.GetComponent<ZiplineTower>();
        var towerB = _rings.GetValueOrDefault(end)?.GetComponent<ZiplineTower>();
        if (towerA == null || towerB == null)
        {
            return false;
        }
        var a = towerA.CableAnchorPointInt;
        var b = towerB.CableAnchorPointInt;
        if (add)
        {
            _navMeshService.AddEdge(NavMeshEdge.CreateGrouped(a, b, GroupId, isRoad: true, EdgeCost));
            _navMeshService.AddEdge(NavMeshEdge.CreateGrouped(b, a, GroupId, isRoad: true, EdgeCost));
            return true;
        }
        _navMeshService.RemoveEdge(NavMeshEdge.CreateGrouped(a, b, GroupId, isRoad: true, EdgeCost));
        _navMeshService.RemoveEdge(NavMeshEdge.CreateGrouped(b, a, GroupId, isRoad: true, EdgeCost));
        if (towerA.IsConnectedTo(towerB))
        {
            _ziplineConnections.Disconnect(towerA, towerB);
            Debug.Log($"[TeleporterStation] ring zipline {start} -> {end} disconnected");
        }
        return true;
    }

    private void RefreshLinks()
    {
        RefreshLinksCore();
    }

    private void RefreshLinksCore()
    {
        var desired = new HashSet<(Vector3Int, Vector3Int)>();
        foreach (var (coordinates, ring) in _rings)
        {
            var partner = ring.Partner;
            if (partner == null || partner == ring)
            {
                continue;
            }
            // both halves registered, finished, charged, unpaused
            if (!_rings.TryGetValue(partner.Coordinates, out var registered) || registered != partner)
            {
                continue;
            }
            if (!CanCarve(ring) || !CanCarve(partner))
            {
                continue;
            }
            // the walkable door; the gate waits for settled claims: DESIGN.md#load-window, DESIGN.md#ring-pairs
            if (LoadComplete
                && !_network.IsReachableByWalk(coordinates)
                && !_network.IsReachableByWalk(partner.Coordinates))
            {
                continue;
            }
            desired.Add(Canonical(coordinates, partner.Coordinates));
        }
        var (added, removed) = ApplyLinks(desired);
        SyncSignatures();
        if (LoadComplete && (added > 0 || removed > 0))
        {
            Debug.Log($"[TeleporterStation] ring routes: {desired.Count} live (+{added} / -{removed})");
        }
    }

    private void SyncSignatures()
    {
        foreach (var (coordinates, ring) in _rings)
        {
            (bool, bool, bool, bool) signature = (ring.IsPaired, HasAnyLink(coordinates),
                ring.IsCharged, ring.IsPaused);
            if (_signatures.TryGetValue(coordinates, out var known) && known == signature)
            {
                continue;
            }
            _signatures[coordinates] = signature;
            if (LoadComplete)
            {
                Debug.Log($"[TeleporterStation] ring {coordinates}: " +
                          $"{(signature.Item1 ? "paired" : "unpaired")}, " +
                          $"route {(signature.Item2 ? "live" : "dark")}, " +
                          $"{(signature.Item3 ? "charged" : "dry")}, " +
                          $"{(signature.Item4 ? "paused" : "unpaused")}");
            }
            RingStateChanged?.Invoke(coordinates);
        }
    }
}
