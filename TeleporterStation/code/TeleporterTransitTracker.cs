using System;
using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.CharacterMovementSystem;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Timberborn.Persistence;
using Timberborn.WalkingSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace TeleporterStation;

// design: DESIGN.md#crossing-detection-and-billing
public class TeleporterTransitTracker : BaseComponent, IAwakableComponent,
    IInitializableEntity, IPostLoadableEntity, IPersistentEntity,
    INavMeshProximityValidator, IPathStartProvider
{
    private static readonly ComponentKey TransitKey = new ComponentKey("TeleporterTransitTracker");

    private static readonly PropertyKey<Vector3> FromPointKey = new PropertyKey<Vector3>("FromPoint");

    private static readonly PropertyKey<Vector3> ToPointKey = new PropertyKey<Vector3>("ToPoint");

    private static readonly PropertyKey<float> LastSpeedKey = new PropertyKey<float>("LastSpeed");

    private const float DefaultResumeSpeed = 3f;

    // JUMP gate, not a crossing proof: DESIGN.md#crossing-detection-and-billing
    private const float SnapJumpDistance = 1f;

    // measured arrival brackets: DESIGN.md#crossing-detection-and-billing
    private const float StillAtStationRadius = 0.75f;
    private const float ApproachRadius = 2.5f;

    // measured departure brackets: DESIGN.md#crossing-detection-and-billing
    private const float DepartureCornerRadius = 1.25f;
    private const float StandingAtStationRadius = 1.1f;

    private readonly TeleporterNetwork _network;

    private Walker _walker;

    private Vector3? _fromPoint;

    private Vector3? _toPoint;

    private float _lastSpeed = DefaultResumeSpeed;

    private Vector3? _lastPosition;

    private Vector3? _lastToPosition;

    // dedups the billing signals: DESIGN.md#crossing-detection-and-billing
    private (Vector3Int Entry, Vector3Int Exit)? _lastBilled;

    // survives the transit: DESIGN.md#crossing-detection-and-billing
    private const int DedupLingerEvents = 8;

    private int _dedupLinger;

    public TeleporterTransitTracker(TeleporterNetwork network)
    {
        _network = network;
    }

    private bool InTransit => _fromPoint.HasValue && _toPoint.HasValue;

    public void Awake()
    {
        _walker = GetComponent<Walker>();
    }

    public void InitializeEntity()
    {
        _walker.PathFollower.MovedAlongPath += OnMovedAlongPath;
    }

    public void Save(IEntitySaver entitySaver)
    {
        if (!InTransit)
        {
            return;
        }
        IObjectSaver component = entitySaver.GetComponent(TransitKey);
        component.Set(FromPointKey, _fromPoint.Value);
        component.Set(ToPointKey, _toPoint.Value);
        component.Set(LastSpeedKey, _lastSpeed);
    }

    public void Load(IEntityLoader entityLoader)
    {
        if (entityLoader.TryGetComponent(TransitKey, out var objectLoader))
        {
            _fromPoint = objectLoader.Get(FromPointKey);
            _toPoint = objectLoader.Get(ToPointKey);
            _lastSpeed = objectLoader.Get(LastSpeedKey);
        }
    }

    public void PostLoadEntity()
    {
        // the pairing may have died while the game was closed: DESIGN.md#crossing-detection-and-billing
        if (InTransit && !_network.MatchLiveLink(_fromPoint.Value, _toPoint.Value, out _, out _))
        {
            ClearTransit();
        }
    }

    public bool IsOnNavMesh()
    {
        return InTransit;
    }

    public bool TryGetPathStart(IDestination destination, List<PathCorner> pathCorners, out Vector3 start)
    {
        if (!InTransit)
        {
            start = default;
            return false;
        }
        start = _toPoint.Value;
        pathCorners.Add(new PathCorner(Transform.position, _lastSpeed, _network.TeleporterGroupId));
        pathCorners.Add(new PathCorner(_toPoint.Value, _lastSpeed, _network.TeleporterGroupId));
        return true;
    }

    private void OnMovedAlongPath(object sender, MovementEventArgs movementEventArgs)
    {
        PathCorner fromCorner = movementEventArgs.From;
        PathCorner toCorner = movementEventArgs.To;
        Vector3? previousPosition = _lastPosition;
        Vector3? previousToCorner = _lastToPosition;
        if (_lastBilled.HasValue && --_dedupLinger <= 0)
        {
            _lastBilled = null;
        }

        if (fromCorner.GroupId == _network.TeleporterGroupId && toCorner.GroupId == _network.TeleporterGroupId)
        {
            if (fromCorner.Speed < float.MaxValue)
            {
                CompleteInstantly(toCorner.Position);
            }
        }
        else if (_network.MatchLiveLink(fromCorner.Position, toCorner.Position, out var entry, out var exit))
        {
            var entryWorld = NavigationCoordinateSystem.GridToWorld(entry);
            var exitWorld = NavigationCoordinateSystem.GridToWorld(exit);
            if (fromCorner.Speed < float.MaxValue)
            {
                // fabricated-corner signature: DESIGN.md#crossing-detection-and-billing
                CompleteInstantly(toCorner.Position);
            }
            _fromPoint = entryWorld;
            _toPoint = exitWorld;
            _lastSpeed = fromCorner.Speed >= float.MaxValue ? DefaultResumeSpeed : fromCorner.Speed;
            Bill(entry, exit);
        }
        else
        {
            ClearTransit();
            BillIfArrivedByLink(fromCorner, previousPosition, previousToCorner);
        }

        BillIfDepartedByLink(previousPosition, previousToCorner);
        BillIfCrossed(previousPosition);
        _lastPosition = Transform.position;
        _lastToPosition = toCorner.Position;
    }

    // span gate + pair dedup: DESIGN.md#crossing-detection-and-billing
    private void Bill(Vector3Int entry, Vector3Int exit)
    {
        var span = exit - entry;
        if (Math.Max(Math.Max(Math.Abs(span.x), Math.Abs(span.y)), Math.Abs(span.z)) < 2)
        {
            return;
        }
        if (_lastBilled == (entry, exit))
        {
            return;
        }
        _network.BillCrossing(entry);
        _lastBilled = (entry, exit);
        _dedupLinger = DedupLingerEvents;
    }

    // ARRIVAL signal: DESIGN.md#crossing-detection-and-billing
    private void BillIfArrivedByLink(PathCorner fromCorner, Vector3? previousPosition, Vector3? previousToCorner)
    {
        if (!previousPosition.HasValue)
        {
            return;
        }
        if (!_network.TryGetStationAt(fromCorner.Position, out var exit))
        {
            return;
        }
        if (Vector3.Distance(previousPosition.Value,
                NavigationCoordinateSystem.GridToWorld(exit)) < StillAtStationRadius)
        {
            return;
        }
        Vector3Int? entry = null;
        if (previousToCorner.HasValue
            && _network.TryGetStationAt(previousToCorner.Value, out var approach)
            && _network.IsLinked(exit, approach))
        {
            entry = approach;
        }
        else
        {
            entry = _network.NearestSibling(exit, previousPosition.Value, ApproachRadius);
        }
        if (entry.HasValue)
        {
            Bill(entry.Value, exit);
        }
    }

    // DEPARTURE signal: DESIGN.md#crossing-detection-and-billing
    private void BillIfDepartedByLink(Vector3? previousPosition, Vector3? previousToCorner)
    {
        if (!previousPosition.HasValue || !previousToCorner.HasValue)
        {
            return;
        }
        if (!_network.TryGetStationNear(previousToCorner.Value, DepartureCornerRadius, out var entry))
        {
            return;
        }
        var entryWorld = NavigationCoordinateSystem.GridToWorld(entry);
        if (Vector3.Distance(previousPosition.Value, entryWorld) > StandingAtStationRadius)
        {
            return;
        }
        var position = Transform.position;
        if (Vector3.Distance(position, entryWorld) < ApproachRadius)
        {
            return;
        }
        var exit = _network.NearestSibling(entry, position, ApproachRadius);
        if (!exit.HasValue)
        {
            return;
        }
        var span = Vector3.Distance(entryWorld, NavigationCoordinateSystem.GridToWorld(exit.Value));
        if (Math.Abs(Vector3.Distance(previousPosition.Value, position) - span) > ApproachRadius)
        {
            return;
        }
        Bill(entry, exit.Value);
    }

    private void BillIfCrossed(Vector3? previousPosition)
    {
        if (!previousPosition.HasValue)
        {
            return;
        }
        var position = Transform.position;
        var distance = Vector3.Distance(previousPosition.Value, position);
        if (distance <= SnapJumpDistance)
        {
            return;
        }
        if (_network.MatchLiveLink(previousPosition.Value, position, out var entry, out var exit))
        {
            Bill(entry, exit);
        }
    }

    private void CompleteInstantly(Vector3 exit)
    {
        Transform.position = exit;
    }

    private void ClearTransit()
    {
        _fromPoint = null;
        _toPoint = null;
        _lastSpeed = DefaultResumeSpeed;
        // The billed pair deliberately survives; see DedupLingerEvents.
    }
}
