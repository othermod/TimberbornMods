using System;
using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.CharacterMovementSystem;
using Timberborn.Coordinates;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Timberborn.TimbermeshAnimations;
using Timberborn.WalkingSystem;
using Timberborn.ZiplineSystem;
using UnityEngine;

namespace TeleporterStation;

// span billing from report order against the plan, tower resolution, and the walk-clock heal: DESIGN.md#crossing-detection-and-billing
public class TeleporterTransitTracker : BaseComponent, IAwakableComponent,
    IInitializableEntity, ILateUpdatableComponent
{
    // one MaxValue corner sample lands above 1e35; honest clocks stay under session hours: bugs/glide
    private const float PoisonedWalkClock = 1e10f;

    private readonly TeleporterNetwork _network;

    private readonly RingNetwork _rings;

    private readonly IBlockService _blockService;

    private Walker _walker;

    private IAnimator _animator;

    private MovementAnimator _movementAnimator;

    // the path's spans in travel order with their plan indices, each billed once: DESIGN.md#crossing-detection-and-billing
    private readonly List<ArmedFlight> _armedFlights = new();

    // the plan's floored corner cells, snapshotted at arm time: DESIGN.md#crossing-detection-and-billing
    private Vector3Int[] _planCells = Array.Empty<Vector3Int>();

    // the corner index the walker is believed to approach; monotonic, and a shared cell resolves to the first match: DESIGN.md#crossing-detection-and-billing
    private int _cursor;

    // the previous report's cursor, seeded at the path's first corner: DESIGN.md#crossing-detection-and-billing
    private int _previousCursor;

    private int _flightIndex;

    public TeleporterTransitTracker(TeleporterNetwork network, RingNetwork rings,
        IBlockService blockService)
    {
        _network = network;
        _rings = rings;
        _blockService = blockService;
    }

    public void Awake()
    {
        _walker = GetComponent<Walker>();
        _animator = GetComponentInChildren<IAnimator>();
        _movementAnimator = GetComponent<MovementAnimator>();
    }

    public void InitializeEntity()
    {
        _walker.StartedNewPath += ArmStationFlights;
        _walker.PathFollower.MovedAlongPath += OnMovedAlongPath;
    }

    // heals a saturated walk clock; LateUpdate so no same-frame re-Play restores it: bugs/glide
    public void LateUpdate()
    {
        if (_animator != null && _movementAnimator != null
            && _animator.Time >= PoisonedWalkClock)
        {
            _movementAnimator.StopAnimatingMovement();
        }
    }

    // the game's own recipe: the corner's floored cell must be the tower's anchor cell: docs/game-internals.md
    private ZiplineTower TowerAt(Vector3 position)
    {
        var cell = CoordinateSystem.WorldToGridInt(position);
        var tower = _blockService.GetBottomObjectComponentAt<ZiplineTower>(cell);
        return tower != null && cell == tower.CableAnchorPointInt ? tower : null;
    }

    // one planned crossing: what to bill and its plan indices: DESIGN.md#crossing-detection-and-billing
    private sealed class ArmedFlight
    {
        internal Vector3Int Billing;
        internal bool IsRing;
        internal int BoardingIndex;
        internal int LandingIndex;
    }

    // the plan is final when the path starts; the span's identity is the plan, its moment the report order: DESIGN.md#crossing-detection-and-billing
    private void ArmStationFlights(object sender, StartedNewPathEventArgs eventArgs)
    {
        _armedFlights.Clear();
        _flightIndex = 0;
        _cursor = 0;
        _previousCursor = 0;
        var corners = _walker.PathCorners;
        _planCells = new Vector3Int[corners.Count];
        for (var i = 0; i < corners.Count; i++)
        {
            _planCells[i] = CoordinateSystem.WorldToGridInt(corners[i].Position);
        }
        for (var i = 0; i + 1 < corners.Count; i++)
        {
            var from = corners[i];
            var to = corners[i + 1];
            if (from.GroupId == _network.GroupId && to.GroupId == _network.GroupId)
            {
                ArmStation(from, to, i);
            }
            else if (from.GroupId == _rings.GroupId && to.GroupId == _rings.GroupId)
            {
                ArmRing(from, to, i);
            }
        }
    }

    // the network's own corners are the instant span; identity is the plan, moment the report order: DESIGN.md#crossing-detection-and-billing
    private void ArmStation(PathCorner from, PathCorner to, int i)
    {
        var entry = TowerAt(from.Position);
        if (entry == null || !_network.StationCoordsOf(entry, out var boarding))
        {
            return;
        }
        var exit = TowerAt(to.Position);
        if (exit == null || exit == entry)
        {
            return;
        }
        _armedFlights.Add(new ArmedFlight
        {
            Billing = boarding,
            BoardingIndex = i,
            LandingIndex = i + 1,
        });
    }

    // the rings' own corners are the instant span, billed the same way: DESIGN.md#travel-every-crossing-is-an-instant-jump
    private void ArmRing(PathCorner from, PathCorner to, int i)
    {
        var entry = TowerAt(from.Position);
        if (entry == null || !_rings.RingCoordsOf(entry, out var boarding))
        {
            return;
        }
        var exit = TowerAt(to.Position);
        if (exit == null || exit == entry)
        {
            return;
        }
        _armedFlights.Add(new ArmedFlight
        {
            IsRing = true,
            Billing = boarding,
            BoardingIndex = i,
            LandingIndex = i + 1,
        });
    }

    // a report whose cursor passed a span's landing while the previous sat at or before its boarding is the crossing, at any speed: DESIGN.md#crossing-detection-and-billing
    private void OnMovedAlongPath(object sender, MovementEventArgs eventArgs)
    {
        var toCell = CoordinateSystem.WorldToGridInt(eventArgs.To.Position);
        var start = _cursor;
        while (_cursor < _planCells.Length && _planCells[_cursor] != toCell)
        {
            _cursor++;
        }
        if (_cursor >= _planCells.Length)
        {
            _cursor = start;
            return;
        }
        while (_flightIndex < _armedFlights.Count)
        {
            var flight = _armedFlights[_flightIndex];
            if (_previousCursor > flight.BoardingIndex || _cursor < flight.LandingIndex)
            {
                break;
            }
            Bill(flight);
        }
        _previousCursor = _cursor;
    }

    private void Bill(ArmedFlight flight)
    {
        _flightIndex++;
        if (flight.IsRing)
        {
            _rings.BillCrossing(flight.Billing);
            return;
        }
        _network.BillCrossing(flight.Billing);
    }
}
