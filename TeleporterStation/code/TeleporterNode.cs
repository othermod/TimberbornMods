using System;
using System.Collections;
using System.Linq;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.BlockingSystem;
using Timberborn.Buildings;
using Timberborn.EntitySystem;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.Persistence;
using Timberborn.TimbermeshAnimations;
using Timberborn.WorldPersistence;
using UnityEngine;

namespace TeleporterStation;

// design: DESIGN.md#lifecycle-and-persistence
public class TeleporterNode : BaseComponent, IAwakableComponent, IFinishedStateListener,
    IFinishedPausable, IPersistentEntity
{
    private const string HorizonNodeName = "Horizon";
    private const string ArmsNodeName = "Arms";
    private const string HorizonAnimation = "Spin";
    // The single source for the charge good's id: the initializer, billing,
    // and the panel's extract row all reference this constant.
    internal const string ExtractGoodId = "Extract";

    // whole crossings per extract: DESIGN.md#charges-and-the-charge-store
    internal const int CrossingsPerCharge = 10;

    private static readonly ComponentKey NodeKey = new ComponentKey("TeleporterNode");
    private static readonly PropertyKey<int> CrossingsCountKey = new PropertyKey<int>("CrossingsSinceCharge");
    private static readonly PropertyKey<bool> ExtractSourceKey = new PropertyKey<bool>("ExtractSource");

    private readonly TeleporterNetwork _network;
    private BlockObject _blockObject;
    private BlockableObject _blockableObject;
    private Inventory _chargeStore;
    private int _crossingsSinceCharge;
    private GameObject _horizon;
    private GameObject _arms;
    private TimbermeshAnimator _animator;
    private Material _voidMaterial;
    private Material _armMaterial;
    private bool _paired;
    private bool _charged;
    private bool _visualsResolved;
    // designated refill source: DESIGN.md#wormhole-siphon
    private bool _extractSource;

    // baked near-white texel divided out so tints land pure: DESIGN.md#horizon-visuals
    private static readonly Vector3 HorizonTexel = new(0.757f, 0.706f, 0.663f);
    private const float VoidBrightness = 0.22f;
    private static readonly int ColorProperty = Shader.PropertyToID("_Color");

    public TeleporterNode(TeleporterNetwork network)
    {
        _network = network;
    }

    public Vector3Int Coordinates => _blockObject.TransformCoordinates(Vector3Int.zero);

    internal Color HorizonColor => DistrictColor(_pairedCenterId);

    // a station pairs with the one network automatically: DESIGN.md#one-network
    public bool IsFinished { get; private set; }

    public bool IsCharged => IsFinished && _charged;

    // live read of the one blocker set vanilla consumers themselves
    // read: DESIGN.md#pause
    internal bool IsPaused => !_blockableObject.IsUnblocked;

    public int Charges => _chargeStore != null ? _chargeStore.AmountInStock(ExtractGoodId) : 0;

    internal Inventory ChargeStore => _chargeStore;

    internal int UnreservedCharges => _chargeStore != null
        ? _chargeStore.UnreservedAmountInStock(ExtractGoodId)
        : 0;

    public bool IsExtractSource => _extractSource;

    public void Awake()
    {
        _blockObject = GetComponent<BlockObject>();
        _blockableObject = GetComponent<BlockableObject>();
        // resolve by component name: DESIGN.md#charges-and-the-charge-store
        _chargeStore = GetComponentsAllocating<Inventory>()
            .FirstOrDefault(s => s.ComponentName == TeleporterChargeStoreInitializer.StoreComponentName);
    }

    public void OnEnterFinishedState()
    {
        IsFinished = true;
        _chargeStore?.Enable();
        _charged = HasStock();
        _network.RegisterStation(Coordinates, this);
        _network.Register(Coordinates, _charged);
        // sync a save-restored pause; the events then drive flips: DESIGN.md#pause
        _network.SetStationPaused(Coordinates, IsPaused);
        _blockableObject.ObjectBlocked += OnBlockableChanged;
        _blockableObject.ObjectUnblocked += OnBlockableChanged;
        _network.PairingChanged += OnPairingChanged;
        _network.NetworkLivenessChanged += OnNetworkLivenessChanged;
        SetPaired(_network.NetworkDistrict()?.District.CenterNodeId ?? -1);
        if (_chargeStore != null)
        {
            _chargeStore.InventoryStockChanged += OnStockChanged;
        }
        ResolveVisuals();
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
        _network.PairingChanged -= OnPairingChanged;
        _network.NetworkLivenessChanged -= OnNetworkLivenessChanged;
        _network.Unregister(Coordinates);
        _horizon = null;
        _arms = null;
        _animator = null;
        _visualsResolved = false;
    }

    private bool HasStock()
    {
        return _chargeStore != null
            && _chargeStore.UnreservedAmountInStock(ExtractGoodId) > 0;
    }

    public int TransitsRemaining => UnreservedCharges * CrossingsPerCharge - _crossingsSinceCharge;

    // every Nth crossing takes one extract: DESIGN.md#charges-and-the-charge-store
    public bool RecordCrossing()
    {
        _crossingsSinceCharge++;
        if (_crossingsSinceCharge < CrossingsPerCharge)
        {
            return false;
        }
        _crossingsSinceCharge = 0;
        if (!HasStock())
        {
            return false;
        }
        _chargeStore.TakeConsumed(new GoodAmount(ExtractGoodId, 1));
        return true;
    }

    private void OnStockChanged(object sender, InventoryStockChangedEventArgs e)
    {
        var charged = HasStock();
        if (charged != _charged)
        {
            _charged = charged;
            _network.SetStationCharged(Coordinates, charged);
            ApplyPaired();
        }
        _network.ScanAutoRefills();
    }

    // any BlockableObject blocker: the player pause button, or automation:
    // DESIGN.md#pause
    private void OnBlockableChanged(object sender, EventArgs e)
    {
        _network.SetStationPaused(Coordinates, IsPaused);
        ApplyPaired();
    }

    internal void RelayOut(int amount)
    {
        if (UnreservedCharges < amount)
        {
            return;
        }
        _chargeStore.TakeConsumed(new GoodAmount(ExtractGoodId, amount));
    }

    // ignores capacity reservations: DESIGN.md#charges-and-the-charge-store
    internal void RelayIn(int amount)
    {
        var free = TeleporterChargeStoreInitializer.ChargeCapacity - Charges;
        if (amount > free)
        {
            amount = free;
        }
        if (amount <= 0)
        {
            return;
        }
        _chargeStore.GiveExistingIgnoringCapacityReservation(new GoodAmount(ExtractGoodId, amount));
    }

    public void ToggleExtractSource()
    {
        _extractSource = !_extractSource;
        Debug.Log($"[TeleporterStation] {Coordinates} extract source " +
                  (_extractSource ? "on" : "off"));
        if (_extractSource)
        {
            _network.ScanAutoRefills();
        }
    }

    public void Save(IEntitySaver entitySaver)
    {
        var component = entitySaver.GetComponent(NodeKey);
        component.Set(CrossingsCountKey, _crossingsSinceCharge);
        component.Set(ExtractSourceKey, _extractSource);
    }

    public void Load(IEntityLoader entityLoader)
    {
        // old saves may lack the blob or its properties: DESIGN.md#lifecycle-and-persistence
        if (!entityLoader.HasComponent(NodeKey))
        {
            return;
        }
        var component = entityLoader.GetComponent(NodeKey);
        if (component.Has(CrossingsCountKey))
        {
            _crossingsSinceCharge = component.Get(CrossingsCountKey);
        }
        if (component.Has(ExtractSourceKey))
        {
            _extractSource = component.Get(ExtractSourceKey);
        }
    }

    // the model may land a frame later: DESIGN.md#horizon-visuals
    private void ResolveVisuals()
    {
        VisualRunner().StartCoroutine(ResolveVisualsCoroutine());
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

    private IEnumerator ResolveVisualsCoroutine()
    {
        for (var frame = 0; frame < 60; frame++)
        {
            _horizon = FindModelChild(HorizonNodeName);
            _arms = FindModelChild(ArmsNodeName);
            _animator = GetComponentInChildren<TimbermeshAnimator>(true);
            if (_horizon != null && _arms != null && _animator != null && _animator.HasAnimation(HorizonAnimation))
            {
                _visualsResolved = true;
                ApplyPaired();
                yield break;
            }
            yield return null;
        }
        Debug.LogWarning($"[TeleporterStation] {Coordinates} visual lookup gave up: " +
                         $"horizon={_horizon != null} arms={_arms != null} " +
                         $"animator={_animator != null} spin={( _animator != null && _animator.HasAnimation(HorizonAnimation))}");
    }

    private void OnNetworkLivenessChanged()
    {
        ApplyPaired();
    }

    private void OnPairingChanged(Vector3Int coordinates, int centerNodeId)
    {
        if (coordinates == Coordinates)
        {
            SetPaired(centerNodeId);
        }
    }

    private int _pairedCenterId = -1;

    private void SetPaired(int centerNodeId)
    {
        _paired = centerNodeId != -1;
        _pairedCenterId = centerNodeId;
        ApplyPaired();
    }

    private void ApplyPaired()
    {
        if (!_visualsResolved)
        {
            return;
        }
        // glow only while charged, unpaused, in a live network: DESIGN.md#pause
        var active = _paired && _charged && !IsPaused && _network.IsNetworkLive();
        _horizon.SetActive(active);
        _arms.SetActive(active);
        _animator.Enabled = active;
        if (!active)
        {
            return;
        }
        _animator.Play(HorizonAnimation);
        var districtColor = DistrictColor(_pairedCenterId);
        var voidColor = districtColor * VoidBrightness;
        voidColor.a = 1f;
        Tint(_horizon, ref _voidMaterial, voidColor);
        Tint(_arms, ref _armMaterial, districtColor);
    }

    // deterministic per-district hue: DESIGN.md#horizon-visuals
    private static Color DistrictColor(int centerNodeId)
    {
        var h = (uint)centerNodeId;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        var hue = (h & 0xFFFFFFu) / 16777216f;
        return Color.HSVToRGB(hue, 0.72f, 0.95f);
    }

    // fresh clone per tint: DESIGN.md#horizon-visuals
    private void Tint(GameObject node, ref Material cached, Color color)
    {
        var renderer = node.GetComponent<MeshRenderer>();
        var shared = renderer.sharedMaterials;
        var previous = cached;
        cached = new Material(shared[0]);
        var tinted = new Color(color.r / HorizonTexel.x, color.g / HorizonTexel.y,
                               color.b / HorizonTexel.z, 1f);
        cached.SetColor(ColorProperty, tinted);
        var materials = (Material[])shared.Clone();
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = cached;
        }
        renderer.sharedMaterials = materials;
        if (previous != null)
        {
            UnityEngine.Object.Destroy(previous);
        }
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
}
