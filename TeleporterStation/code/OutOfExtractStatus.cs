using System;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.GameDistricts;
using Timberborn.Hauling;
using Timberborn.Localization;
using Timberborn.StatusSystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace TeleporterStation;

// design: DESIGN.md#statuses-and-vanilla-icon-suppression
public class OutOfExtractStatus : TickableComponent, IAwakableComponent,
    IInitializableEntity, IPostLoadableEntity, IDeletableEntity
{
    private const string SpriteName = "LackOfResources";
    private const string HaulersSpriteName = "OutOfHaulersRange";
    private const string DeliverLocKey = "Teleporter.Status.OutOfExtract";
    private const string HaulersLocKey = "Teleporter.Status.OutOfExtract.Haulers";
    private const string RemoteNoSourceLocKey = "Teleporter.Status.OutOfExtract.Remote.NoSource";
    private const string RemoteNoneStockedLocKey = "Teleporter.Status.OutOfExtract.Remote.NoneStocked";
    private const string ShortLocKey = "Teleporter.Status.OutOfExtract.Short";

    private readonly ILoc _loc;
    private readonly TeleporterNetwork _network;
    private TeleporterNode _node;
    private HaulCandidate _haulCandidate;
    private DistrictBuilding _districtBuilding;
    private StatusToggle _deliverStatus;
    private StatusToggle _haulersStatus;
    private StatusToggle _remoteNoSourceStatus;
    private StatusToggle _remoteNoneStockedStatus;

    public OutOfExtractStatus(ILoc loc, TeleporterNetwork network)
    {
        _loc = loc;
        _network = network;
    }

    public void Awake()
    {
        _node = GetComponent<TeleporterNode>();
        _haulCandidate = GetComponent<HaulCandidate>();
        _districtBuilding = GetComponent<DistrictBuilding>();
        _deliverStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            SpriteName, _loc.T(DeliverLocKey), _loc.T(ShortLocKey));
        _haulersStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            HaulersSpriteName, _loc.T(HaulersLocKey), _loc.T(ShortLocKey));
        _remoteNoSourceStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            SpriteName, _loc.T(RemoteNoSourceLocKey), _loc.T(ShortLocKey));
        _remoteNoneStockedStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            SpriteName, _loc.T(RemoteNoneStockedLocKey), _loc.T(ShortLocKey));
    }

    public void InitializeEntity()
    {
        var subject = GetComponent<StatusSubject>();
        subject.RegisterStatus(_deliverStatus);
        subject.RegisterStatus(_haulersStatus);
        subject.RegisterStatus(_remoteNoSourceStatus);
        subject.RegisterStatus(_remoteNoneStockedStatus);
        _network.PairingChanged += OnPairingChanged;
        _network.StationChargeChanged += OnStationChargeChanged;
        _haulCandidate.InHaulingCenterRangeChanged += OnHaulingRangeChanged;
        _districtBuilding.ReassignedDistrict += OnDistrictReassigned;
    }

    private void OnPairingChanged(Vector3Int coordinates, int districtCenterNodeId)
    {
        if (coordinates == _node.Coordinates)
        {
            Evaluate();
        }
    }

    private void OnStationChargeChanged(Vector3Int coordinates)
    {
        if (coordinates == _node.Coordinates)
        {
            Evaluate();
        }
    }

    private void OnHaulingRangeChanged(object sender, EventArgs e)
    {
        Evaluate();
    }

    private void OnDistrictReassigned(object sender, EventArgs e)
    {
        Evaluate();
    }

    // teardown seam: DESIGN.md#lifecycle-and-persistence
    public void DeleteEntity()
    {
        _network.PairingChanged -= OnPairingChanged;
        _network.StationChargeChanged -= OnStationChargeChanged;
        _haulCandidate.InHaulingCenterRangeChanged -= OnHaulingRangeChanged;
        _districtBuilding.ReassignedDistrict -= OnDistrictReassigned;
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
        var dry = _node.IsFinished && !_node.IsCharged
                  && _network.NetworkDistrict() != null;
        if (!dry)
        {
            DeactivateAll();
            return;
        }
        if (_network.IsReachableByRealPath(_node.Coordinates))
        {
            var haulingPost = _haulCandidate.IsInHaulingCenterRange;
            _deliverStatus.Toggle(haulingPost);
            _haulersStatus.Toggle(!haulingPost);
            _remoteNoSourceStatus.Deactivate();
            _remoteNoneStockedStatus.Deactivate();
            return;
        }
        _deliverStatus.Deactivate();
        _haulersStatus.Deactivate();
        var designated = _network.HasDesignatedSource(_node.Coordinates);
        _remoteNoSourceStatus.Toggle(!designated);
        _remoteNoneStockedStatus.Toggle(designated);
    }

    private void DeactivateAll()
    {
        _deliverStatus.Deactivate();
        _haulersStatus.Deactivate();
        _remoteNoSourceStatus.Deactivate();
        _remoteNoneStockedStatus.Deactivate();
    }
}
