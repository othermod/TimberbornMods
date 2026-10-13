using System;
using System.Collections.Generic;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace TeleporterStation;

// ring pairs, one-to-one routes independent of the station network: DESIGN.md#ring-pairs
public class RingNetwork : TeleporterLinkIndex, ILoadableSingleton, IPostLoadableSingleton
{
    // the group literal ResolveGroup stores into the base's GroupName
    private const string GroupLiteral = "TeleporterRing";

    // tubeway-rate travel: the crossing costs distance x this: DESIGN.md#ring-transit
    internal const float TubewayUnitCost = 0.25f;

    internal const float TravellerScale = 0.4f;

    // the hide fires near an anchor, whose height shifts the grid cell a layer: DESIGN.md#ring-transit
    private const float TimedLinkRadius = 3f;

    private readonly TeleporterNetwork _network;
    private readonly EntityService _entityService;

    private readonly Dictionary<Vector3Int, RingNode> _rings = new();
    private readonly Dictionary<Vector3Int, (bool Paired, bool Live, bool Charged, bool Paused)> _signatures = new();

    // with the network's settled claims this closes the load window: DESIGN.md#load-window
    private bool _postLoaded;

    // fires at the end of a refresh when a ring's signature changed: DESIGN.md#ring-pairs
    internal event Action<Vector3Int> RingStateChanged;

    public RingNetwork(TeleporterNetwork network, EntityService entityService,
        INavMeshService navMeshService,
        NavMeshGroupService navMeshGroupService, NodeIdService nodeIdService,
        TerrainNavMeshSource terrainNavMeshSource,
        InstantTerrainNavMeshSource instantTerrainNavMeshSource,
        PreviewTerrainNavMeshSource previewTerrainNavMeshSource,
        RoadNavMeshSource roadNavMeshSource, InstantRoadNavMeshSource instantRoadNavMeshSource,
        PreviewRoadNavMeshSource previewRoadNavMeshSource)
        : base(navMeshService, navMeshGroupService, nodeIdService, terrainNavMeshSource,
              instantTerrainNavMeshSource, previewTerrainNavMeshSource, roadNavMeshSource,
              instantRoadNavMeshSource, previewRoadNavMeshSource)
    {
        _network = network;
        _entityService = entityService;
    }

    public void Load()
    {
        ResolveGroup(GroupLiteral);
        // the claim map stays real-paths-only: DESIGN.md#path-reachability
        _network.SkipNavGroupInClaimMap(GroupId);
        _network.ClaimMapRefreshed += OnClaimMapRefreshed;
        Debug.Log($"[TeleporterStation] ring nav mesh group '{GroupName}' = {GroupId}");
    }

    // the window closes at the later of our PostLoad and the network's settled claims: DESIGN.md#load-window
    public void PostLoad()
    {
        _postLoaded = true;
        RefreshLinks();
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

    // tubeway-rate travel: the crossing costs distance x this (walk-seconds): DESIGN.md#ring-transit
    internal override float EdgeCost(Vector3Int start, Vector3Int end)
    {
        var dx = end.x - start.x;
        var dy = end.y - start.y;
        return Mathf.Sqrt(dx * dx + dy * dy) * TubewayUnitCost;
    }

    // every flight corner carries the constant tubeway rate: DESIGN.md#ring-transit
    internal override float TransitSpeed(Vector3Int entry, Vector3Int exit)
    {
        return 1f / TubewayUnitCost;
    }

    // DIRECTIONAL: the ring near the position boards, its partner is the exit: DESIGN.md#ring-transit
    internal override bool TryGetTimedLink(Vector3 world, out Vector3Int entry, out Vector3Int exit)
    {
        var ring = RingNear(world, TimedLinkRadius);
        var partner = ring?.Partner;
        if (ring == null || partner == null)
        {
            entry = default;
            exit = default;
            return false;
        }
        entry = ring.Coordinates;
        exit = partner.Coordinates;
        return true;
    }

    // the plate's top center, the anchor the flight line and chain follow: DESIGN.md#ring-transit
    internal override Vector3 AnchorWorld(Vector3Int coordinates)
    {
        return RingAt(coordinates)?.AnchorWorld()
               ?? CoordinateSystem.GridToWorldCentered(coordinates);
    }

    private RingNode RingNear(Vector3 world, float maxDistance)
    {
        RingNode best = null;
        var bestDistance = maxDistance;
        foreach (var ring in _rings.Values)
        {
            var distance = Vector3.Distance(world, ring.AnchorWorld());
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = ring;
            }
        }
        return best;
    }

    // the flying traveller is a mini clone of the entry ring's horizon: DESIGN.md#ring-transit
    internal GameObject SpawnTraveller(Vector3 position)
    {
        var ring = RingNear(position, TimedLinkRadius);
        var source = ring?.OrbNode;
        if (source == null)
        {
            return null;
        }
        var clone = UnityEngine.Object.Instantiate(source);
        clone.SetActive(true);
        clone.transform.position = position;
        clone.transform.localScale = Vector3.one * TravellerScale;
        return clone;
    }

    internal GameObject SpawnSweep(Vector3 position)
    {
        var ring = RingNear(position, TimedLinkRadius);
        var source = ring?.SweepNode;
        if (source == null)
        {
            return null;
        }
        var clone = UnityEngine.Object.Instantiate(source);
        clone.SetActive(true);
        // the rings stay centered on the plate: DESIGN.md#ring-transit
        clone.transform.position = ring.AnchorWorld();
        return clone;
    }

    // the visual captures the entry ring to find the remote's plate: DESIGN.md#ring-transit
    internal RingNode NearestRing(Vector3 position) =>
        RingNear(position, TimedLinkRadius);

    // the route flies in the network district's hue: DESIGN.md#ring-transit
    internal Color TravellerColor =>
        TeleporterNode.DistrictColor(_network.NetworkDistrict()?.District.CenterNodeId ?? -1);

    internal bool IsRouteLive(Vector3Int coordinates)
    {
        return HasAnyLink(coordinates);
    }

    internal override void BillCrossing(Vector3Int entry)
    {
        _rings.GetValueOrDefault(entry)?.RecordCrossing();
    }

    private bool CanCarve(RingNode ring)
    {
        return ring.IsFinished && !ring.IsPaused && ring.IsCharged;
    }

    private void RefreshLinks()
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
