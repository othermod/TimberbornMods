using System;
using Timberborn.BaseComponentSystem;
using Timberborn.BuildingsReachability;
using Timberborn.EntitySystem;
using Timberborn.StatusSystem;
using Timberborn.TickSystem;
using UnityEngine;

namespace TeleporterStation;

// suppresses the vanilla unconnected icon for paired rings only: DESIGN.md#ring-pairs
public class RingConnectivityBlocker : TickableComponent, IAwakableComponent,
    IInitializableEntity, IPostLoadableEntity, IUnconnectedBuildingBlocker, IDeletableEntity
{
    private readonly RingNetwork _rings;
    private RingNode _node;
    private bool _blocked;

    public RingConnectivityBlocker(RingNetwork rings)
    {
        _rings = rings;
    }

    // the vanilla icon reads this at init and subscribes to the change event
    public bool IsUnconnectedBlocked => _blocked;

    public event EventHandler IsUnconnectedBlockedChanged;

    public void Awake()
    {
        _node = GetComponent<RingNode>();
    }

    public void InitializeEntity()
    {
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
        SetBlocked(_node.IsFinished && _node.IsPaired);
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
