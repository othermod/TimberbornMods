using Timberborn.BlockObjectTools;
using Timberborn.BlockSystem;
using Timberborn.BlueprintSystem;
using Timberborn.EntitySystem;
using Timberborn.FactionSystem;
using Timberborn.GameFactionSystem;
using Timberborn.SingletonSystem;
using Timberborn.TemplateSystem;
using Timberborn.ToolSystem;
using UnityEngine;

namespace TeleporterStation;

// the pairing flow: a built ring arms free-remote placement, a partnerless remote waits for a built ring: DESIGN.md#ring-pairs
public class RingPairingFlow : ILoadableSingleton
{
    private readonly ToolService _toolService;
    private readonly BlockObjectToolFactory _toolFactory;
    private readonly BlockObjectPlacerService _placerService;
    private readonly BlockObjectToolDescriber _describer;
    private readonly TemplateNameMapper _templateNameMapper;
    private readonly FactionService _factionService;
    private readonly RingNetwork _rings;
    private readonly RingPairingState _state;
    private readonly EventBus _eventBus;

    private BlockObjectTool _tool;

    public RingPairingFlow(ToolService toolService, BlockObjectToolFactory toolFactory,
        BlockObjectPlacerService placerService, BlockObjectToolDescriber describer,
        TemplateNameMapper templateNameMapper, FactionService factionService,
        RingNetwork rings, RingPairingState state, EventBus eventBus)
    {
        _toolService = toolService;
        _toolFactory = toolFactory;
        _placerService = placerService;
        _describer = describer;
        _templateNameMapper = templateNameMapper;
        _factionService = factionService;
        _rings = rings;
        _state = state;
        _eventBus = eventBus;
    }

    public void Load()
    {
        _eventBus.Register(this);
    }

    // panel button: a built ring summons its free remote, a partnerless remote waits for a built ring
    internal void BeginRemotePlacement(RingNode anchor)
    {
        _state.Anchor = anchor;
        if (!anchor.IsRemote)
        {
            _toolService.SwitchTool(Tool());
        }
        Debug.Log($"[TeleporterStation] pairing armed for ring {anchor.Coordinates} " +
                  $"(remote: {anchor.IsRemote})");
    }

    // the remote blueprint carries no placement spec so no menu can list it; the tool runs on a synthetic one over a copy: DESIGN.md#ring-pairs
    private BlockObjectTool Tool()
    {
        if (_tool != null)
        {
            return _tool;
        }
        var faction = _factionService.Current.Id;
        var remote = _templateNameMapper.GetTemplate(RemoteRingSpec.TemplateNamePrefix + faction);
        var template = new PlaceableBlockObjectSpec
        {
            Blueprint = new Blueprint(remote.Blueprint, null, null),
            Layout = BlockObjectLayout.Single,
            CustomPivot = new CustomPivotSpec(),
        };
        _tool = _toolFactory.Create(template,
            _placerService.GetMatchingPlacer(template.GetSpec<BlockObjectSpec>()), _describer);
        return _tool;
    }

    [OnEvent]
    public void OnRingInitialized(EntityInitializedEvent evt)
    {
        var ring = evt.Entity.GetComponent<RingNode>();
        if (ring == null || !_rings.LoadComplete)
        {
            return;
        }
        var blockObject = evt.Entity.GetComponent<BlockObject>();
        if (blockObject == null || blockObject.IsPreview)
        {
            return;
        }
        var anchor = _state.Anchor;
        if (anchor == null || anchor == ring)
        {
            return;
        }
        _state.Anchor = null;
        if (anchor.IsRemote != ring.IsRemote
            && RingNode.InRange(anchor.Coordinates, ring.Coordinates))
        {
            anchor.SetPartner(ring);
            ring.SetPartner(anchor);
            Debug.Log($"[TeleporterStation] ring pair linked: " +
                      $"{anchor.Coordinates} <-> {ring.Coordinates}");
            EndRemoteSession();
            return;
        }
        Debug.LogWarning($"[TeleporterStation] remote link failed (anchor gone, out of " +
                         $"range, or same role); ring {ring.Coordinates} stays partnerless");
    }

    // only our remote tool's exit abandons the arm; the menu tool's exit is our own switching
    [OnEvent]
    public void OnToolExited(ToolExitedEvent evt)
    {
        if (evt.Tool == _tool)
        {
            if (_state.Anchor != null)
            {
                Debug.Log("[TeleporterStation] remote placement abandoned");
            }
            _state.Anchor = null;
        }
    }

    // a completed pair ends the session through the game's own Esc path: docs/game-internals.md
    private void EndRemoteSession()
    {
        if (_toolService.ActiveTool == _tool)
        {
            _toolService.SwitchToDefaultTool();
        }
    }
}
