using System;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.Localization;
using Timberborn.StatusSystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace TeleporterStation;

// the unpaired states; paired rings belong to the dry status and the blocker: DESIGN.md#ring-pairs
public class RingUnpairedStatus : TickableComponent, IAwakableComponent,
    IInitializableEntity, IPostLoadableEntity, IDeletableEntity
{
    private const string LinkSpriteName = "UnconnectedBuilding";
    private const string FuelSpriteName = "LackOfResources";
    private const string BuildLocKey = "Ring.Status.Unpaired.Master.Build";
    private const string FuelLocKey = "Ring.Status.Unpaired.Master.Fuel";
    private const string MasterLocKey = "Ring.Status.Unpaired.Master";
    private const string MasterShortLocKey = "Ring.Status.Unpaired.Master.Short";
    private const string RemoteLocKey = "Ring.Status.Unpaired.Remote";
    private const string RemoteShortLocKey = "Ring.Status.Unpaired.Remote.Short";

    private readonly ILoc _loc;
    private readonly RingNetwork _rings;
    private RingNode _node;
    private StatusToggle _buildStatus;
    private StatusToggle _fuelStatus;
    private StatusToggle _masterStatus;
    private StatusToggle _remoteStatus;

    public RingUnpairedStatus(ILoc loc, RingNetwork rings)
    {
        _loc = loc;
        _rings = rings;
    }

    public void Awake()
    {
        _node = GetComponent<RingNode>();
        // one short text for all built-ring stages: one ticker row counts them all
        var masterShort = _loc.T(MasterShortLocKey);
        _buildStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            LinkSpriteName, _loc.T(BuildLocKey), masterShort);
        _fuelStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            FuelSpriteName, _loc.T(FuelLocKey), masterShort);
        _masterStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            LinkSpriteName, _loc.T(MasterLocKey), masterShort);
        _remoteStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            LinkSpriteName, _loc.T(RemoteLocKey), _loc.T(RemoteShortLocKey));
    }

    public void InitializeEntity()
    {
        var subject = GetComponent<StatusSubject>();
        subject.RegisterStatus(_buildStatus);
        subject.RegisterStatus(_fuelStatus);
        subject.RegisterStatus(_masterStatus);
        subject.RegisterStatus(_remoteStatus);
        _rings.RingStateChanged += OnRingStateChanged;
    }

    // teardown seam: DESIGN.md#lifecycle-and-persistence
    public void DeleteEntity()
    {
        _rings.RingStateChanged -= OnRingStateChanged;
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

    private void OnRingStateChanged(Vector3Int coordinates)
    {
        if (coordinates == _node.Coordinates)
        {
            Evaluate();
        }
    }

    private void Evaluate()
    {
        var master = !_node.IsPaired && !_node.IsRemote;
        _buildStatus.Toggle(master && !_node.IsFinished);
        _fuelStatus.Toggle(master && _node.IsFinished && !_node.IsCharged);
        _masterStatus.Toggle(master && _node.IsFinished && _node.IsCharged);
        _remoteStatus.Toggle(!_node.IsPaired && _node.IsRemote);
    }
}
