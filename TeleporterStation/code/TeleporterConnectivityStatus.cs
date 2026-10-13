using System;
using Timberborn.BaseComponentSystem;
using Timberborn.BuildingsReachability;
using Timberborn.EntitySystem;
using Timberborn.Localization;
using Timberborn.StatusSystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace TeleporterStation;

// status publishers and vanilla-icon suppression, one problem one publisher: DESIGN.md#statuses-and-vanilla-icon-suppression
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
        // the dry-beyond-the-paths story is OutOfExtractStatus's: DESIGN.md#statuses-and-vanilla-icon-suppression
        var dryBeyondPaths = finished && !conflict
                             && !_node.IsCharged
                             && _network.NetworkDistrict() != null
                             && !_network.IsReachableByRealPath(_node.Coordinates);
        // a charged live-network member works through the wormhole: DESIGN.md#statuses-and-vanilla-icon-suppression
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
