using System;
using Timberborn.BaseComponentSystem;
using Timberborn.CharacterModelSystem;
using Timberborn.CharacterMovementSystem;
using Timberborn.EntitySystem;
using Timberborn.WalkingSystem;
using UnityEngine;

namespace TeleporterStation;

// the wormhole traveller: instant orb at the plate, rings burst and retract: DESIGN.md#ring-transit
public class RingTransitVisual : BaseComponent, IAwakableComponent, IInitializableEntity,
    IUpdatableComponent, IDeletableEntity
{
    private const float TwoPi = 2f * Mathf.PI;

    // retract travel: the stack sinks this far below its plate to hide: DESIGN.md#ring-transit
    private const float SweepDrop = 0.8f;

    // ring motion: displacement in walk-equivalent units; the orb covers 1/TubewayUnitCost times more ground per second, so fly scales by TubewayUnitCost: DESIGN.md#ring-transit
    private const float RetractDistance = 1.5f;
    private const float RiseDistance = 0.45f;
    private const float HoldDistance = 0.7f;
    private const float RiseDrop = 0.75f;

    // rematerialize starts this close to the remote's plate so the model shows on it: DESIGN.md#ring-transit
    private const float RematerializeRadius = 0.75f;

    // a rematerialize whose walk simply ends here finishes instead of hanging: DESIGN.md#ring-transit
    private const float RetractStallSeconds = 0.75f;

    // the orb breathes while in flight: DESIGN.md#ring-transit
    private const float PulseCyclesPerSecond = 1.6f;
    private const float PulseAmplitude = 0.08f;

    // the orb flies stretched along its line (a matter-stream segment): DESIGN.md#ring-transit
    private const float StreamStretch = 2.5f;

    // glow via the game's own highlight emission channel: DESIGN.md#ring-transit
    private const float OrbEmission = 2.5f;

    // the orb's fixed wormhole blue; the rings keep the district hue: DESIGN.md#ring-transit
    private static readonly Color OrbColor = new(0.15f, 0.75f, 1f);

    // a crossing that stops moving (a path ending on the ring) must not stay hidden: DESIGN.md#ring-transit
    private const float StallSeconds = 1.5f;

    private static readonly int ColorProperty = Shader.PropertyToID("_Color");
    private static readonly int EmissionColorProperty = Shader.PropertyToID("_EmissionColor");

    private enum Phase { Idle, Fly, Rematerialize }

    private readonly RingNetwork _rings;

    private readonly TeleporterNetwork _network;

    private MovementAnimator _movementAnimator;

    private NavMeshObserver _navMeshObserver;

    private CharacterModel _characterModel;

    private Phase _phase = Phase.Idle;

    private GameObject _traveller;

    private GameObject _sweep;

    private Vector3 _sweepBase;

    private Vector3 _beginPosition;

    private RingNode _ring;

    private Material _orbMaterial;

    private Material _sweepMaterial;

    private Vector3 _lastPosition;

    private float _stallSeconds;

    private float _phaseSeconds;

    public RingTransitVisual(RingNetwork rings, TeleporterNetwork network)
    {
        _rings = rings;
        _network = network;
    }

    public void Awake()
    {
        _movementAnimator = GetComponent<MovementAnimator>();
        _navMeshObserver = GetComponent<NavMeshObserver>();
        _characterModel = GetComponent<CharacterModel>();
    }

    public void InitializeEntity()
    {
        _movementAnimator.GroupIdUpdated += OnGroupIdUpdated;
        _navMeshObserver.PlacedOnNavMesh += OnPlacedOnNavMesh;
    }

    // teardown seam: DESIGN.md#lifecycle-and-persistence
    public void DeleteEntity()
    {
        _movementAnimator.GroupIdUpdated -= OnGroupIdUpdated;
        _navMeshObserver.PlacedOnNavMesh -= OnPlacedOnNavMesh;
        EndTransit("delete");
    }

    public void Update()
    {
        if (_phase == Phase.Idle || Time.deltaTime <= 0f)
        {
            return;
        }
        var position = _characterModel.Position;
        _phaseSeconds += Time.deltaTime;
        if (_phase == Phase.Fly)
        {
            Fly(position);
            return;
        }
        Rematerialize(position);
    }

    // orb follows the walk and the entry rings retract while it departs: DESIGN.md#ring-transit
    private void Fly(Vector3 position)
    {
        var exit = _ring?.Partner;
        var exitAnchor = exit != null ? exit.AnchorWorld() : Vector3.zero;
        if (_traveller != null)
        {
            _traveller.transform.position = position;
            var direction = exitAnchor - position;
            if (direction.sqrMagnitude > 0.001f)
            {
                _traveller.transform.rotation = Quaternion.LookRotation(direction);
            }
            var pulse = 1f + PulseAmplitude
                * Mathf.Sin(TwoPi * PulseCyclesPerSecond * _phaseSeconds);
            var cross = RingNetwork.TravellerScale * pulse;
            _traveller.transform.localScale = new Vector3(cross, cross,
                cross * StreamStretch);
        }
        Retract(position, RingNetwork.TubewayUnitCost);
        if ((position - _lastPosition).sqrMagnitude > 0.0001f)
        {
            _stallSeconds = 0f;
        }
        else
        {
            _stallSeconds += Time.deltaTime;
            if (_stallSeconds > StallSeconds)
            {
                EndTransit("stall");
                return;
            }
        }
        _lastPosition = position;
        // close to the remote's plate: rematerialize on it, not past it: DESIGN.md#ring-transit
        if (exit != null && (position - exitAnchor).sqrMagnitude
                < RematerializeRadius * RematerializeRadius)
        {
            BeginRematerialize();
        }
    }

    // the model is back; the rings sink as it walks off: DESIGN.md#ring-transit
    private void Rematerialize(Vector3 position)
    {
        Retract(position, 1f);
        if ((position - _lastPosition).sqrMagnitude > 0.0001f)
        {
            _stallSeconds = 0f;
        }
        else
        {
            _stallSeconds += Time.deltaTime;
            if (_stallSeconds > RetractStallSeconds)
            {
                FinishRematerialize();
                return;
            }
        }
        _lastPosition = position;
        if ((position - _beginPosition).magnitude >= RetractDistance)
        {
            FinishRematerialize();
        }
    }

    private void FinishRematerialize()
    {
        DestroySweep();
        _ring = null;
        _phase = Phase.Idle;
    }

    private void OnGroupIdUpdated(object sender, GroupIdUpdatedEventArgs e)
    {
        var ringFlip = e.GroupId == _rings.GroupId;
        var stationFlip = e.GroupId == _network.GroupId;
        if (ringFlip || stationFlip)
        {
            // a flip into a flight group mid-Fly (chained pairs) keeps the traveller: DESIGN.md#travel-timed-crossings
            if (_phase == Phase.Idle)
            {
                BeginTransit(ringFlip);
            }
        }
        else if (_phase == Phase.Fly)
        {
            BeginRematerialize();
        }
    }

    // a snap back onto the mesh means the crossing died under the traveller: DESIGN.md#ring-transit
    private void OnPlacedOnNavMesh(object sender, EventArgs e)
    {
        EndTransit("placed");
    }

    private void BeginTransit(bool withOrb)
    {
        // the model vanishes at the plate; only rings fly the orb: DESIGN.md#travel-timed-crossings
        _phase = Phase.Fly;
        _phaseSeconds = 0f;
        _stallSeconds = 0f;
        _beginPosition = _characterModel.Position;
        _ring = withOrb ? _rings.NearestRing(_beginPosition) : null;
        _characterModel.Hide();
        if (withOrb)
        {
            SpawnOrb(_beginPosition);
            SpawnSweep();
        }
    }

    // the model reappears inside rings that sink into the remote plate: DESIGN.md#ring-transit
    private void BeginRematerialize()
    {
        if (_phase != Phase.Fly)
        {
            return;
        }
        _characterModel.Show();
        DestroyTraveller();
        DestroySweep();
        _phase = Phase.Rematerialize;
        _phaseSeconds = 0f;
        _stallSeconds = 0f;
        _beginPosition = _characterModel.Position;
        // stations vanish only; nothing bursts at the exit
        if (_ring != null)
        {
            SpawnSweep();
        }
    }

    private void SpawnOrb(Vector3 position)
    {
        _traveller = _rings.SpawnTraveller(position);
        if (_traveller != null)
        {
            TintClone(_traveller, OrbColor, ref _orbMaterial);
        }
    }

    // the clone centers on the nearest ring's plate: DESIGN.md#ring-transit
    private void SpawnSweep()
    {
        _sweep = _rings.SpawnSweep(_beginPosition);
        if (_sweep != null)
        {
            _sweepBase = _sweep.transform.position;
            TintClone(_sweep, _rings.TravellerColor, ref _sweepMaterial);
        }
    }

    // tint via _Color, glow via the highlight emission channel (one material per clone): DESIGN.md#ring-transit
    private void TintClone(GameObject clone, Color color, ref Material material)
    {
        var renderer = clone.GetComponentInChildren<MeshRenderer>();
        if (renderer == null)
        {
            return;
        }
        var tinted = new Color(
            color.r / TeleporterNode.HorizonTexel.x,
            color.g / TeleporterNode.HorizonTexel.y,
            color.b / TeleporterNode.HorizonTexel.z, 1f);
        if (material != null)
        {
            UnityEngine.Object.Destroy(material);
        }
        material = new Material(renderer.sharedMaterial);
        material.SetColor(ColorProperty, tinted);
        material.SetColor(EmissionColorProperty, color * OrbEmission);
        var materials = renderer.sharedMaterials;
        for (var i = 0; i < materials.Length; i++)
        {
            materials[i] = material;
        }
        renderer.sharedMaterials = materials;
    }

    private void Retract(Vector3 position, float displacementScale)
    {
        var displacement = (position - _beginPosition).magnitude * displacementScale;
        float offset;
        if (displacement < RiseDistance)
        {
            offset = Mathf.Lerp(-RiseDrop, 0f, displacement / RiseDistance);
        }
        else if (displacement < HoldDistance)
        {
            offset = 0f;
        }
        else
        {
            offset = Mathf.Lerp(0f, -SweepDrop, Mathf.Min(
                (displacement - HoldDistance) / (RetractDistance - HoldDistance),
                1f));
        }
        if (_sweep != null)
        {
            _sweep.transform.position = _sweepBase + Vector3.up * offset;
        }
    }

    private void DestroyTraveller()
    {
        if (_traveller != null)
        {
            UnityEngine.Object.Destroy(_traveller);
            _traveller = null;
        }
        if (_orbMaterial != null)
        {
            UnityEngine.Object.Destroy(_orbMaterial);
            _orbMaterial = null;
        }
    }

    private void DestroySweep()
    {
        if (_sweep != null)
        {
            UnityEngine.Object.Destroy(_sweep);
            _sweep = null;
        }
        if (_sweepMaterial != null)
        {
            UnityEngine.Object.Destroy(_sweepMaterial);
            _sweepMaterial = null;
        }
    }

    private void EndTransit(string reason)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }
        _characterModel.Show();
        DestroyTraveller();
        DestroySweep();
        _ring = null;
        _phase = Phase.Idle;
    }

}
