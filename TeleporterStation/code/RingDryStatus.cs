using System;
using Timberborn.BaseComponentSystem;
using Timberborn.EntitySystem;
using Timberborn.Localization;
using Timberborn.StatusSystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace TeleporterStation;

// the paired dry state; unpaired rings keep the vanilla icon: DESIGN.md#ring-pairs
public class RingDryStatus : TickableComponent, IAwakableComponent,
    IInitializableEntity, IPostLoadableEntity, IDeletableEntity
{
    private const string SpriteName = "LackOfResources";
    private const string LocKey = "Ring.Status.OutOfExtract";
    private const string ShortLocKey = "Ring.Status.OutOfExtract.Short";

    private readonly ILoc _loc;
    private readonly RingNetwork _rings;
    private RingNode _node;
    private StatusToggle _dryStatus;

    public RingDryStatus(ILoc loc, RingNetwork rings)
    {
        _loc = loc;
        _rings = rings;
    }

    public void Awake()
    {
        _node = GetComponent<RingNode>();
        _dryStatus = StatusToggle.CreateNormalStatusWithAlertAndFloatingIcon(
            SpriteName, _loc.T(LocKey), _loc.T(ShortLocKey));
    }

    public void InitializeEntity()
    {
        var subject = GetComponent<StatusSubject>();
        subject.RegisterStatus(_dryStatus);
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
        _dryStatus.Toggle(_node.IsFinished && _node.IsPaired && !_node.IsCharged);
    }
}
