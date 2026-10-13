using System;
using System.Collections.Generic;
using Timberborn.AssetSystem;
using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.CameraSystem;
using Timberborn.EntitySystem;
using Timberborn.RootProviders;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.ToolSystem;
using UnityEngine;

namespace TeleporterStation;

// pair leader line and placement probe, the game's district-connection recipes: DESIGN.md#ring-pairs
public class RingConnectionLine : ILoadableSingleton, IUpdatableSingleton
{
    private static readonly int UnscaledTimeProperty = Shader.PropertyToID("_UnscaledTime");

    // the game's own values: docs/game-ui-reference.md
    private const string PrefabPath = "UI/Markers/DistrictConnectionDrawer";
    private const float ArcAngleRad = 30f * (float)Math.PI / 180f;
    private const int CurvePoints = 50;
    private const float LineCutoff = 0.4f;
    private const float TiltStartingAngle = 35f;
    private const float MaxTilt = 45f;

    private readonly IAssetLoader _assets;
    private readonly RootObjectProvider _rootObjectProvider;
    private readonly CameraService _camera;
    private readonly EventBus _eventBus;
    private readonly RingNetwork _rings;
    private readonly RingPairingState _state;

    private LineRenderer _line;
    private bool _shown;
    private bool _simple;
    private RingNode _selected;
    private RingNode _previewRing;
    private Vector3 _cachedStart;
    private Vector3 _cachedEnd;
    private Quaternion _startingRotation;
    private Vector3 _upVector;
    private Vector3 _rightVector;
    private Vector3 _forwardVector;
    private Vector3 _start;

    public RingConnectionLine(IAssetLoader assets, RootObjectProvider rootObjectProvider,
        CameraService camera, EventBus eventBus, RingNetwork rings, RingPairingState state)
    {
        _assets = assets;
        _rootObjectProvider = rootObjectProvider;
        _camera = camera;
        _eventBus = eventBus;
        _rings = rings;
        _state = state;
    }

    public void Load()
    {
        _eventBus.Register(this);
        var prefab = _assets.LoadSafe<LineRenderer>(PrefabPath);
        if (prefab == null)
        {
            Debug.LogError("[TeleporterStation] district line prefab missing; pair lines disabled");
            return;
        }
        _line = UnityEngine.Object.Instantiate(prefab,
            _rootObjectProvider.CreateRootObject("RingConnectionLine").transform);
        _line.enabled = false;
    }

    // placement pair wins over selection; the probe tracks it even without a line
    public void UpdateSingleton()
    {
        RingNode from = null;
        RingNode to = null;
        var anchor = _state.Anchor;
        if (anchor != null && _previewRing != null && anchor != _previewRing)
        {
            from = anchor;
            to = _previewRing;
        }
        _rings.SetProbeEdge(from != null ? from.Coordinates : null,
            to != null ? to.Coordinates : null);
        if (_line == null)
        {
            return;
        }
        if (from == null && _selected != null && _selected.IsPaired)
        {
            from = _selected;
            to = _selected.Partner;
        }
        if (from == null || to == null)
        {
            if (_shown)
            {
                Hide();
            }
            return;
        }
        var start = from.AnchorWorld();
        var end = to.AnchorWorld();
        if (!_shown || start != _cachedStart || end != _cachedEnd)
        {
            _cachedStart = start;
            _cachedEnd = end;
            Build(start, end);
        }
        Shader.SetGlobalFloat(UnscaledTimeProperty, Time.unscaledTime);
        if (_simple)
        {
            SimpleRotation();
        }
        else
        {
            Rotation();
        }
    }

    [OnEvent]
    public void OnRingSelected(SelectableObjectSelectedEvent evt)
    {
        var ring = evt.SelectableObject.GetComponent<RingNode>();
        if (ring != null && ring.IsPaired)
        {
            _selected = ring;
        }
    }

    [OnEvent]
    public void OnRingUnselected(SelectableObjectUnselectedEvent evt)
    {
        if (_selected != null && evt.SelectableObject.GetComponent<RingNode>() == _selected)
        {
            _selected = null;
        }
    }

    // the remote ghost announces itself like any ring entity: DESIGN.md#ring-pairs
    [OnEvent]
    public void OnRingInitialized(EntityInitializedEvent evt)
    {
        var ring = evt.Entity.GetComponent<RingNode>();
        if (ring == null)
        {
            return;
        }
        var blockObject = evt.Entity.GetComponent<BlockObject>();
        if (blockObject != null && blockObject.IsPreview)
        {
            _previewRing = ring;
        }
    }

    [OnEvent]
    public void OnEntityDeleted(EntityDeletedEvent evt)
    {
        var ring = evt.Entity.GetComponent<RingNode>();
        if (ring == null)
        {
            return;
        }
        if (ring == _previewRing)
        {
            _previewRing = null;
        }
        if (ring == _selected)
        {
            _selected = null;
        }
    }

    [OnEvent]
    public void OnToolExited(ToolExitedEvent evt)
    {
        if (evt.Tool is BlockObjectTool tool && tool.Template != null
            && (tool.Template.GetSpec<RingSpec>() != null
                || tool.Template.GetSpec<RemoteRingSpec>() != null))
        {
            _previewRing = null;
        }
    }

    private void Hide()
    {
        _shown = false;
        _line.enabled = false;
    }

    private void Build(Vector3 start, Vector3 end)
    {
        _start = start;
        if (Mathf.Abs(start.x - end.x) <= 0.01f && Mathf.Abs(start.z - end.z) <= 0.01f)
        {
            SetRenderer(new[] { start, end });
            SetLineDirection();
            _simple = true;
            return;
        }
        BuildCurvedLine(start, end);
        SetLineDirection();
        _rightVector = _line.transform.right;
        _upVector = _line.transform.up;
        _forwardVector = _line.transform.forward;
        _simple = false;
    }

    private void SetRenderer(Vector3[] points)
    {
        _line.positionCount = points.Length;
        _line.SetPositions(points);
        _line.enabled = true;
        _shown = true;
    }

    private void BuildCurvedLine(Vector3 start, Vector3 end)
    {
        var center = ArcCenterPoint(start, end);
        var a = start - center;
        var b = end - center;
        var cut = (float)(Math.Tan(LineCutoff / a.magnitude) / ArcAngleRad);
        var arcFrom = Vector3.Slerp(a, b, cut);
        var arcTo = Vector3.Slerp(a, b, 1f - cut);
        var points = new List<Vector3>(CurvePoints + 1);
        for (var i = 0; i <= CurvePoints; i++)
        {
            points.Add(center + Vector3.Slerp(arcFrom, arcTo, i / (float)CurvePoints));
        }
        SetRenderer(points.ToArray());
    }

    private static Vector3 ArcCenterPoint(Vector3 start, Vector3 end)
    {
        if (start.y > end.y)
        {
            (start, end) = (end, start);
        }
        var direction = end - start;
        var angle = Math.Asin(direction.y / direction.magnitude);
        if (angle <= Math.PI / 2.0 - ArcAngleRad)
        {
            return ArcCenterFromTwoPoints(start, end);
        }
        return ArcCenterFromTangentCircle(start, direction);
    }

    private static Vector3 ArcCenterFromTwoPoints(Vector3 start, Vector3 end)
    {
        var span = end - start;
        var axis = Vector3.Cross(span, Vector3.down);
        var perpendicular = Quaternion.AngleAxis(90f, axis) * span;
        var radius = span.magnitude * 0.5f / (float)Math.Tan(ArcAngleRad);
        return (start + end) * 0.5f + perpendicular.normalized * radius;
    }

    private static Vector3 ArcCenterFromTangentCircle(Vector3 start, Vector3 direction)
    {
        var xx = Math.Pow(direction.x, 2.0);
        var yy = Math.Pow(direction.y, 2.0);
        var zz = Math.Pow(direction.z, 2.0);
        var radius = (xx + yy + zz) / (2.0 * Math.Sqrt(xx + zz));
        return start + new Vector3(direction.x, 0f, direction.z).normalized * (float)radius;
    }

    private void SetLineDirection()
    {
        var direction = (_cachedEnd - _cachedStart).normalized;
        var side = Vector3.Cross(direction, Vector3.up);
        var forward = Vector3.Cross(direction, side);
        _startingRotation = Quaternion.LookRotation(forward, Vector3.Cross(Vector3.up, side));
        _line.transform.rotation = _startingRotation;
    }

    private void SimpleRotation()
    {
        var forward = _camera.Transform.position - _start;
        forward.y = 0f;
        _line.transform.rotation = Quaternion.LookRotation(forward);
    }

    private void Rotation()
    {
        var cameraForward = _camera.Transform.forward.normalized;
        var t = (Vector3.SignedAngle(_forwardVector, cameraForward,
                    Vector3.Cross(_forwardVector, cameraForward)) + TiltStartingAngle)
                / (180f - TiltStartingAngle);
        var sign = Mathf.Sign(Vector3.Dot(-_rightVector, cameraForward));
        var horizontalCamera = cameraForward;
        horizontalCamera.y = 0f;
        var horizontalUp = _upVector;
        horizontalUp.y = 0f;
        var ratio = 1f - Mathf.Pow(
            Vector3.Dot(horizontalCamera.normalized, horizontalUp.normalized), 4f);
        var tilt = Mathf.Lerp(0f, MaxTilt, t) * sign * ratio;
        _line.transform.rotation = Quaternion.AngleAxis(-tilt, _upVector) * _startingRotation;
    }
}
