using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.CharacterMovementSystem;
using Timberborn.EntitySystem;
using Timberborn.Navigation;
using Timberborn.Persistence;
using Timberborn.TimbermeshAnimations;
using Timberborn.WalkingSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace TeleporterStation;

// unified transit and billing for every wormhole crossing: DESIGN.md#travel-timed-crossings
public class TeleporterTransitTracker : BaseComponent, IAwakableComponent,
    IInitializableEntity, IPostLoadableEntity, IPersistentEntity,
    INavMeshProximityValidator, IPathStartProvider, ILateUpdatableComponent
{
    private static readonly ComponentKey TransitKey = new ComponentKey("TeleporterTransitTracker");

    private static readonly PropertyKey<Vector3> FromPointKey = new PropertyKey<Vector3>("FromPoint");

    private static readonly PropertyKey<Vector3> ToPointKey = new PropertyKey<Vector3>("ToPoint");

    private static readonly PropertyKey<float> LastSpeedKey = new PropertyKey<float>("LastSpeed");

    private const float DefaultResumeSpeed = 3f;

    // one MaxValue corner sample lands above 1e35; honest clocks stay under session hours: bugs/glide
    private const float PoisonedWalkClock = 1e10f;

    private readonly TeleporterLinkIndex[] _systems;

    private Walker _walker;

    private IAnimator _animator;

    private MovementAnimator _movementAnimator;

    private Vector3? _fromPoint;

    private Vector3? _toPoint;

    private float _lastSpeed = DefaultResumeSpeed;

    // the system whose link is being crossed (its group resumes the path)
    private TeleporterLinkIndex _transitSystem;

    // the engaged link's cells; IsOnNavMesh answers honestly from their liveness
    private Vector3Int _entryCell;

    private Vector3Int _exitCell;

    // dedups the billing signals: DESIGN.md#crossing-detection-and-billing
    private (Vector3Int Entry, Vector3Int Exit)? _lastBilled;

    // survives the transit: DESIGN.md#crossing-detection-and-billing
    private const int DedupLingerEvents = 8;

    private int _dedupLinger;

    public TeleporterTransitTracker(TeleporterNetwork network, RingNetwork rings)
    {
        _systems = new TeleporterLinkIndex[] { network, rings };
    }

    private bool InTransit => _fromPoint.HasValue && _toPoint.HasValue;

    public void Awake()
    {
        _walker = GetComponent<Walker>();
        _animator = GetComponentInChildren<IAnimator>();
        _movementAnimator = GetComponent<MovementAnimator>();
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
        if (!InTransit)
        {
            return;
        }
        foreach (var system in _systems)
        {
            if (system.MatchLiveLink(_fromPoint.Value, _toPoint.Value, out var entry, out var exit))
            {
                _transitSystem = system;
                _entryCell = entry;
                _exitCell = exit;
                return;
            }
        }
        ClearTransit();
    }

    // honest while the engaged link lives; the game's rescue owns a dead one: DESIGN.md#travel-timed-crossings
    public bool IsOnNavMesh()
    {
        return InTransit && _transitSystem != null
            && _transitSystem.IsLinked(_entryCell, _exitCell);
    }

    public bool TryGetPathStart(IDestination destination, List<PathCorner> pathCorners, out Vector3 start)
    {
        if (!InTransit)
        {
            start = default;
            return false;
        }
        var groupId = _transitSystem != null ? _transitSystem.GroupId : _systems[0].GroupId;
        start = _toPoint.Value;
        pathCorners.Add(new PathCorner(Transform.position, _lastSpeed, groupId));
        pathCorners.Add(new PathCorner(_toPoint.Value, _lastSpeed, groupId));
        return true;
    }

    private void OnMovedAlongPath(object sender, MovementEventArgs movementEventArgs)
    {
        PathCorner fromCorner = movementEventArgs.From;
        PathCorner toCorner = movementEventArgs.To;
        if (_lastBilled.HasValue && --_dedupLinger <= 0)
        {
            _lastBilled = null;
        }
        // a flight's corner pairs always carry the owning group on BOTH sides; anything else while in transit is the walk leaving (often inside one tick, so the leaving pair itself may never report): DESIGN.md#travel-timed-crossings
        var inFlight = false;
        foreach (var system in _systems)
        {
            if (fromCorner.GroupId == system.GroupId && toCorner.GroupId == system.GroupId)
            {
                inFlight = true;
                break;
            }
        }
        if (inFlight)
        {
            if (!InTransit)
            {
                Engage(fromCorner, toCorner);
            }
        }
        else if (InTransit)
        {
            ClearTransit();
        }
    }

    // engaged once per crossing at the first in-flight corner pair; the bill lands here: DESIGN.md#crossing-detection-and-billing
    private void Engage(PathCorner fromCorner, PathCorner toCorner)
    {
        var system = SystemOf(fromCorner.GroupId);
        if (system == null)
        {
            return;
        }
        if (!system.MatchLiveLink(fromCorner.Position, toCorner.Position, out var entry, out var exit)
            && !system.TryGetTimedLink(toCorner.Position, out entry, out exit)
            && !system.TryGetTimedLink(fromCorner.Position, out entry, out exit))
        {
            return;
        }
        // the resume bridges toward the flight's exit corner: DESIGN.md#crossing-detection-and-billing
        _fromPoint = fromCorner.Position;
        _toPoint = toCorner.Position;
        _lastSpeed = system.TransitSpeed(entry, exit);
        _transitSystem = system;
        _entryCell = entry;
        _exitCell = exit;
        Bill(system, entry, exit);
    }

    private TeleporterLinkIndex SystemOf(int groupId)
    {
        foreach (var system in _systems)
        {
            if (system.GroupId == groupId)
            {
                return system;
            }
        }
        return null;
    }

    // heals a saturated walk clock; LateUpdate so no same-frame re-Play restores it: bugs/glide
    public void LateUpdate()
    {
        if (_animator == null || _movementAnimator == null || _animator.Time < PoisonedWalkClock)
        {
            return;
        }
        _movementAnimator.StopAnimatingMovement();
    }

    // pair dedup: DESIGN.md#crossing-detection-and-billing
    private void Bill(TeleporterLinkIndex owner, Vector3Int entry, Vector3Int exit)
    {
        if (_lastBilled == (entry, exit))
        {
            return;
        }
        owner.BillCrossing(entry);
        _lastBilled = (entry, exit);
        _dedupLinger = DedupLingerEvents;
    }

    private void ClearTransit()
    {
        _fromPoint = null;
        _toPoint = null;
        _lastSpeed = DefaultResumeSpeed;
        _transitSystem = null;
        // the billed pair deliberately survives: DESIGN.md#crossing-detection-and-billing
    }
}
