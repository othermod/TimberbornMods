using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlockingSystem;
using Timberborn.BuildingRange;
using Timberborn.ConstructionMode;
using Timberborn.EntitySystem;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.PathSystem;
using Timberborn.Persistence;
using Timberborn.TemplateSystem;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace TeleporterStation;

// one end of an explicit pair: DESIGN.md#ring-pairs
public class RingNode : BaseComponent, IAwakableComponent, IFinishedStateListener,
    IPersistentEntity, IBuildingWithRange,
    IPathConnectionEnforcer
{
    // the route indicator (the platform's pale ring inscription): DESIGN.md#ring-pairs
    private const string RouteRingNodeName = "RouteRing";

    // clone source nodes, hidden on the building: DESIGN.md#ring-transit
    private const string OrbNodeName = "Orb";
    private const string SweepNodeName = "TransportRings";

    // pair range in cells: docs/tuning.md
    internal const int MaxRange = 30;

    // outline band thickness in cells: docs/tuning.md
    internal const int OutlineThickness = 2;

    private static readonly ComponentKey NodeKey = new ComponentKey("RingNode");
    private static readonly PropertyKey<int> CrossingsCountKey = new PropertyKey<int>("CrossingsSinceCharge");
    private static readonly PropertyKey<RingNode> PartnerKey = new PropertyKey<RingNode>("Partner");

    private readonly RingNetwork _rings;
    private readonly IBlockService _blockService;
    private readonly ReferenceSerializer _referenceSerializer;
    private readonly ConstructionModeService _constructionModeService;
    private readonly RingPairingState _state;

    private BlockObject _blockObject;
    private BlockableObject _blockableObject;
    private BlockObjectRange _blockObjectRange;
    private TemplateSpec _templateSpec;
    private Inventory _chargeStore;
    private int _crossingsSinceCharge;
    private bool _charged;
    private RingNode _partner;
    private GameObject _routeRing;
    private GameObject _orb;
    private GameObject _sweep;
    private Vector3? _anchorOffset;

    public RingNode(RingNetwork rings, IBlockService blockService,
        ReferenceSerializer referenceSerializer,
        ConstructionModeService constructionModeService, RingPairingState state)
    {
        _rings = rings;
        _blockService = blockService;
        _referenceSerializer = referenceSerializer;
        _constructionModeService = constructionModeService;
        _state = state;
    }

    public Vector3Int Coordinates => _blockObject.TransformCoordinates(Vector3Int.zero);

    public bool IsFinished { get; private set; }

    // the remote's charge is the pool's, read through the partner: DESIGN.md#ring-pairs
    public bool IsCharged => IsRemote ? Partner != null && Partner.IsCharged
        : IsFinished && _charged;

    // live read of the one blocker vanilla consumers themselves read: DESIGN.md#pause
    internal bool IsPaused => !_blockableObject.IsUnblocked;

    public bool IsPaired => _partner != null;

    internal RingNode Partner => _partner;

    // the free half of a pair, flagged by its own spec: DESIGN.md#ring-pairs
    internal bool IsRemote => _templateSpec != null
        && _templateSpec.GetSpec<RemoteRingSpec>() != null;

    // one pool and one crossing counter per pair; the built half holds both: DESIGN.md#ring-pairs
    internal Inventory ChargeStore => PoolStore;

    private Inventory PoolStore => IsRemote ? Partner?._chargeStore : _chargeStore;

    private RingNode PoolNode => IsRemote ? Partner : this;

    // pool extract converts at the shared rate; the pair's one counter advances per crossing: DESIGN.md#charges-and-the-charge-store
    public int TransitsRemaining => (PoolStore != null
        ? PoolStore.UnreservedAmountInStock(TeleporterNode.ExtractGoodId) : 0)
        * TeleporterNode.CrossingsPerCharge - PoolCounter;

    private int PoolCounter => PoolNode != null ? PoolNode._crossingsSinceCharge : 0;

    public void Awake()
    {
        _blockObject = GetComponent<BlockObject>();
        _blockableObject = GetComponent<BlockableObject>();
        _blockObjectRange = GetComponent<BlockObjectRange>();
        _templateSpec = GetComponent<TemplateSpec>();
        // resolve by component name: DESIGN.md#charges-and-the-charge-store
        _chargeStore = GetComponentsAllocating<Inventory>()
            .FirstOrDefault(s => s.ComponentName == RingChargeStoreInitializer.StoreComponentName);
        // the model ships with every node active: DESIGN.md#ring-transit
        HideCloneSources();
    }

    private void HideCloneSources()
    {
        _orb = HiddenCloneSource(OrbNodeName, _orb);
        _sweep = HiddenCloneSource(SweepNodeName, _sweep);
    }

    private GameObject HiddenCloneSource(string name, GameObject current)
    {
        var node = current != null ? current : FindModelChild(name);
        if (node != null)
        {
            node.SetActive(false);
        }
        return node;
    }

    public void OnEnterFinishedState()
    {
        IsFinished = true;
        _chargeStore?.Enable();
        _charged = HasStock();
        _rings.Register(Coordinates, this);
        // sync a save-restored pause: DESIGN.md#pause
        _rings.SetRingPaused(Coordinates, IsPaused);
        _blockableObject.ObjectBlocked += OnBlockableChanged;
        _blockableObject.ObjectUnblocked += OnBlockableChanged;
        if (_chargeStore != null)
        {
            _chargeStore.InventoryStockChanged += OnStockChanged;
        }
        _rings.RingStateChanged += OnRingStateChanged;
        ResolveRouteRing();
    }

    public void OnExitFinishedState()
    {
        IsFinished = false;
        _blockableObject.ObjectBlocked -= OnBlockableChanged;
        _blockableObject.ObjectUnblocked -= OnBlockableChanged;
        if (_chargeStore != null)
        {
            _chargeStore.InventoryStockChanged -= OnStockChanged;
            _chargeStore.Disable();
        }
        _rings.RingStateChanged -= OnRingStateChanged;
        _rings.Unregister(Coordinates);
        _routeRing = null;
        _orb = null;
        _sweep = null;
    }

    private bool HasStock()
    {
        var store = PoolStore;
        return store != null
            && store.UnreservedAmountInStock(TeleporterNode.ExtractGoodId) > 0;
    }

    // every Nth crossing takes one extract; billing lands at the entry ring, the counter is the pair's: DESIGN.md#charges-and-the-charge-store
    public bool RecordCrossing()
    {
        if (IsRemote)
        {
            return Partner != null && Partner.RecordCrossing();
        }
        _crossingsSinceCharge++;
        if (_crossingsSinceCharge < TeleporterNode.CrossingsPerCharge)
        {
            return false;
        }
        _crossingsSinceCharge = 0;
        if (!HasStock())
        {
            return false;
        }
        _chargeStore.TakeConsumed(new GoodAmount(TeleporterNode.ExtractGoodId, 1));
        return true;
    }

    private void OnStockChanged(object sender, InventoryStockChangedEventArgs e)
    {
        var charged = HasStock();
        if (charged != _charged)
        {
            _charged = charged;
            _rings.SetRingCharged(Coordinates, charged);
        }
    }

    // any BlockableObject blocker, player pause or automation: DESIGN.md#pause
    private void OnBlockableChanged(object sender, EventArgs e)
    {
        _rings.SetRingPaused(Coordinates, IsPaused);
    }

    private void OnRingStateChanged(Vector3Int coordinates)
    {
        if (coordinates == Coordinates)
        {
            ApplyRouteRing();
        }
    }

    internal void SetPartner(RingNode partner)
    {
        _partner = partner;
        _rings.OnPairingChanged();
    }

    // the network clears the survivor on remote demolition and refreshes once
    internal void ClearPartner()
    {
        _partner = null;
    }

    // horizontal distance only; the drawn range uses the same metric: DESIGN.md#ring-pairs
    internal static bool InRange(Vector3Int a, Vector3Int b)
    {
        var dx = a.x - b.x;
        var dy = a.y - b.y;
        return dx * dx + dy * dy <= MaxRange * MaxRange;
    }

    // the traveller visual clones these nodes: DESIGN.md#ring-transit
    internal GameObject OrbNode => _orb;

    internal GameObject SweepNode => _sweep;

    // plate-top center via the RouteRing node; bounds walk is the fallback: DESIGN.md#ring-pairs
    internal Vector3 AnchorWorld()
    {
        if (_routeRing != null)
        {
            return _routeRing.transform.position;
        }
        var position = _blockObject.GameObject.transform.position;
        if (_anchorOffset.HasValue)
        {
            return position + _anchorOffset.Value;
        }
        var bounds = new Bounds(position, Vector3.zero);
        var found = false;
        foreach (var renderer in _blockObject.GameObject.GetComponentsInChildren<MeshRenderer>(true))
        {
            bounds.Encapsulate(renderer.bounds);
            found = true;
        }
        if (!found)
        {
            return position;
        }
        _anchorOffset = bounds.center - position;
        return bounds.center;
    }

    public void Save(IEntitySaver entitySaver)
    {
        var component = entitySaver.GetComponent(NodeKey);
        component.Set(CrossingsCountKey, _crossingsSinceCharge);
        if (_partner != null)
        {
            component.Set(PartnerKey, _partner, _referenceSerializer.Of<RingNode>());
        }
    }

    public void Load(IEntityLoader entityLoader)
    {
        if (!entityLoader.HasComponent(NodeKey))
        {
            return;
        }
        var component = entityLoader.GetComponent(NodeKey);
        if (component.Has(CrossingsCountKey))
        {
            _crossingsSinceCharge = component.Get(CrossingsCountKey);
        }
        if (component.Has(PartnerKey))
        {
            _partner = component.Get(PartnerKey, _referenceSerializer.Of<RingNode>());
            _rings.OnPairingChanged();
        }
    }

    // the inscription shows only while the route can carry someone: DESIGN.md#ring-pairs
    private void ResolveRouteRing()
    {
        VisualRunner().StartCoroutine(ResolveRouteRingCoroutine());
    }

    private CoroutineRunner VisualRunner()
    {
        var runner = _blockObject.GameObject.GetComponent<CoroutineRunner>();
        if (runner == null)
        {
            runner = _blockObject.GameObject.AddComponent<CoroutineRunner>();
        }
        return runner;
    }

    private IEnumerator ResolveRouteRingCoroutine()
    {
        for (var frame = 0; frame < 60; frame++)
        {
            _routeRing = FindModelChild(RouteRingNodeName);
            HideCloneSources();
            if (_routeRing != null)
            {
                ApplyRouteRing();
                yield break;
            }
            yield return null;
        }
        Debug.LogWarning($"[TeleporterStation] ring {Coordinates} route ring lookup gave up");
    }

    // the gate's rule, scoped like its Start/End: the ring meets the path network only at its door tile: DESIGN.md#ring-pairs
    public bool CanConnectPath(Vector3Int origin, Vector3Int target)
    {
        if (_blockObject == null || !_blockObject.IsFinished
            || _blockObject.PositionedEntrance == null)
        {
            return false;
        }
        var door = _blockObject.PositionedEntrance.Coordinates;
        var other = origin == Coordinates ? target : origin;
        return (origin == Coordinates || target == Coordinates)
            && other == door && IsFinishedPathAt(door);
    }

    private bool IsFinishedPathAt(Vector3Int coordinates)
    {
        var pathObject = _blockService.GetPathObjectAt(coordinates);
        // the Path slot holds any ground object (trees use All); only DynamicPathModelSpec tiles render dirt: docs/game-internals.md
        return pathObject != null && pathObject.IsFinished
            && pathObject.GetComponent<DynamicPathModelSpec>() != null;
    }

    private void ApplyRouteRing()
    {
        if (_routeRing == null)
        {
            return;
        }
        var active = IsCharged && !IsPaused && IsPaired && _rings.IsRouteLive(Coordinates);
        _routeRing.SetActive(active);
    }

    private GameObject FindModelChild(string name)
    {
        foreach (var transform in _blockObject.GameObject.GetComponentsInChildren<Transform>(true))
        {
            if (transform.name == name)
            {
                return transform.gameObject;
            }
        }
        return null;
    }

    private sealed class CoroutineRunner : MonoBehaviour { }

    // shared by both ring templates so the game unions their ranges: DESIGN.md#ring-pairs
    public string RangeName => "TeleporterRing";

    // one outline on screen, only while a pair can still be formed: DESIGN.md#ring-pairs
    public IEnumerable<Vector3Int> GetBlocksInRange()
    {
        if (IsPaired)
        {
            return Enumerable.Empty<Vector3Int>();
        }
        var draws = _blockObject.IsPreview
            ? _state.Anchor == null
            : _state.Anchor != null && _state.Anchor.Coordinates == Coordinates;
        if (!draws)
        {
            return Enumerable.Empty<Vector3Int>();
        }
        return OutlineCells();
    }

    private IEnumerable<Vector3Int> OutlineCells()
    {
        var center = Coordinates;
        var outer = MaxRange * MaxRange;
        var inner = (MaxRange - OutlineThickness) * (MaxRange - OutlineThickness);
        var finishedOnly = !_constructionModeService.InConstructionMode;
        foreach (var cell in _blockObjectRange
                     .GetBlocksOnTerrainOrStackableInRectangularRadius(MaxRange, finishedOnly))
        {
            var dx = cell.x - center.x;
            var dy = cell.y - center.y;
            var squared = dx * dx + dy * dy;
            if (squared <= outer && squared > inner)
            {
                yield return cell;
            }
        }
    }

    public IEnumerable<BaseComponent> GetObjectsInRange()
    {
        return Enumerable.Empty<BaseComponent>();
    }
}
