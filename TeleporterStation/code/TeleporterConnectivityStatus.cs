using System;
using Timberborn.BaseComponentSystem;
using Timberborn.BuildingsReachability;
using Timberborn.EntitySystem;
using Timberborn.Localization;
using Timberborn.StatusSystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace TeleporterStation;

// The connectivity explanations the vanilla icon cannot give (the same
// ownership idiom the dry statuses follow: one problem, one publisher,
// mutually exclusive predicates, vanilla icon suppressed only where the
// mod explains better):
//
// - CONFLICT (a save from a multi-district version holds teleporters on
//   several districts): every network is offline by design
//   (DESIGN.md#one-network) on every station, and the vanilla icon's
//   implied advice, build paths, is exactly backwards; the conflict
//   heals by demolition.
// - OFFLINE (the network is dark: no path-connected member holds
//   extract, DESIGN.md#network-liveness): charged stations beyond the
//   paths show the refill advice. The dry anchor itself is covered by
//   the dry statuses (OutOfExtractStatus); a charged path-connected
//   station would make the network live, so the predicate cannot
//   overlap it.
// - DRY BEYOND THE PATHS (a dry station no path reaches, while the
//   network exists): OutOfExtractStatus owns the story with its siphon
//   advice, so the vanilla icon is suppressed there too. With no
//   network at all nothing is suppressed: a lone wilderness station is
//   exactly what the vanilla icon describes.
//
// - WORKING MEMBER (charged, live network): no vanilla icon. The icon
//   reads DistrictBuilding.InstantDistrict, a cached assignment that the
//   game refreshes only on nav update events; the mod's load-window edge
//   writes skip those events by design (DESIGN.md#load-window), so a
//   station connected purely through wormhole edges can stay unassigned.
//   The network's assignment pulse (TeleporterNetwork.PostLoad) heals
//   that through the game's own pipeline; this blocker is the guarantee
//   for as long as it has not landed, because the horizon glow is the
//   honest indicator and the vanilla message would call a working
//   station broken.
//
// Everywhere else the vanilla "isn't connected to any district center by
// paths" icon stands: a station with no network at all is exactly what
// it describes.
public class TeleporterConnectivityStatus : TickableComponent, IAwakableComponent,
    IInitializableEntity, IPostLoadableEntity, IUnconnectedBuildingBlocker, IDeletableEntity
{
    private const string ConflictSpriteName = "UnconnectedBuilding";
    private const string ConflictLocKey = "Teleporter.Status.Conflict";
    private const string ConflictShortLocKey = "Teleporter.Status.Conflict.Short";
    private const string OfflineSpriteName = "UnconnectedBuilding";
    private const string OfflineLocKey = "Teleporter.Status.NetworkOffline";
    private const string OfflineShortLocKey = "Teleporter.Status.NetworkOffline.Short";

    private readonly ILoc _loc;
    private readonly TeleporterNetwork _network;
    private TeleporterNode _node;
    private StatusToggle _conflictStatus;
    private StatusToggle _offlineStatus;
    private bool _blocked;

    public TeleporterConnectivityStatus(ILoc loc, TeleporterNetwork network)
    {
        _loc = loc;
        _network = network;
    }

    // the vanilla icon reads this at init and subscribes to the change event
    public bool IsUnconnectedBlocked => _blocked;

    public event EventHandler IsUnconnectedBlockedChanged;

    public void Awake()
    {
        _node = GetComponent<TeleporterNode>();
        _conflictStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            ConflictSpriteName, _loc.T(ConflictLocKey), _loc.T(ConflictShortLocKey));
        _offlineStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            OfflineSpriteName, _loc.T(OfflineLocKey), _loc.T(OfflineShortLocKey));
    }

    public void InitializeEntity()
    {
        var subject = GetComponent<StatusSubject>();
        subject.RegisterStatus(_conflictStatus);
        subject.RegisterStatus(_offlineStatus);
        _network.PairingChanged += OnPairingChanged;
        _network.NetworkLivenessChanged += OnNetworkLivenessChanged;
    }

    private void OnPairingChanged(Vector3Int coordinates, int districtCenterNodeId)
    {
        if (coordinates == _node.Coordinates)
        {
            Evaluate();
        }
    }

    private void OnNetworkLivenessChanged()
    {
        Evaluate();
    }

    // teardown seam: DESIGN.md#lifecycle-and-persistence
    public void DeleteEntity()
    {
        _network.PairingChanged -= OnPairingChanged;
        _network.NetworkLivenessChanged -= OnNetworkLivenessChanged;
    }

    public override void Tick()
    {
        Evaluate();
    }

    // a loaded save starts paused: DESIGN.md#statuses-and-vanilla-icon-suppression
    public void PostLoadEntity()
    {
        Evaluate();
    }

    private void Evaluate()
    {
        var finished = _node.IsFinished;
        var conflict = finished && _network.IsConflicted;
        var offline = finished && !conflict
                      && _network.NetworkDistrict() != null
                      && !_network.IsNetworkLive()
                      && _node.IsCharged
                      && !_network.IsReachableByRealPath(_node.Coordinates);
        // the dry-beyond-the-paths story is OutOfExtractStatus's; the
        // blocker only keeps the vanilla icon out of its way
        var dryBeyondPaths = finished && !conflict
                             && !_node.IsCharged
                             && _network.NetworkDistrict() != null
                             && !_network.IsReachableByRealPath(_node.Coordinates);
        // a charged member of a live network works through the wormhole
        // even with no path to its door; the glow is the indicator
        var working = finished && !conflict
                      && _network.NetworkDistrict() != null
                      && _network.IsNetworkLive()
                      && _node.IsCharged;
        _conflictStatus.Toggle(conflict);
        _offlineStatus.Toggle(offline);
        SetBlocked(conflict || offline || dryBeyondPaths || working);
    }

    private void SetBlocked(bool blocked)
    {
        if (_blocked == blocked)
        {
            return;
        }
        _blocked = blocked;
        IsUnconnectedBlockedChanged?.Invoke(this, EventArgs.Empty);
    }
}
