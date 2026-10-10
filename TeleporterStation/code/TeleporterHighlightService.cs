using System.Collections.Generic;
using Timberborn.SelectionSystem;
using Timberborn.SingletonSystem;
using UnityEngine;

namespace TeleporterStation;

// design: DESIGN.md#selection-highlight
public class TeleporterHighlightService : ILoadableSingleton, ILateUpdatableSingleton
{
    private readonly Highlighter _highlighter;
    private readonly EventBus _eventBus;
    private readonly EntitySelectionService _entitySelectionService;
    private readonly TeleporterNetwork _network;

    private readonly HashSet<TeleporterNode> _highlighted = new();
    private TeleporterNode _selected;
    private bool _dirty;

    public TeleporterHighlightService(Highlighter highlighter, EventBus eventBus,
        EntitySelectionService entitySelectionService, TeleporterNetwork network)
    {
        _highlighter = highlighter;
        _eventBus = eventBus;
        _entitySelectionService = entitySelectionService;
        _network = network;
    }

    public void Load()
    {
        _eventBus.Register(this);
        _network.PairingChanged += OnPairingChanged;
        _network.StationPauseChanged += OnStationPauseChanged;
    }

    [OnEvent]
    public void OnSelectableObjectSelected(SelectableObjectSelectedEvent selectedEvent)
    {
        _selected = _entitySelectionService.SelectedObject.GetComponent<TeleporterNode>();
        _dirty = true;
    }

    [OnEvent]
    public void OnSelectableObjectUnselected(SelectableObjectUnselectedEvent unselectedEvent)
    {
        _selected = null;
        _dirty = true;
    }

    private void OnPairingChanged(Vector3Int coordinates, int districtCenterNodeId)
    {
        _dirty = true;
    }

    // a pause flip may not change any pairing: DESIGN.md#pause
    private void OnStationPauseChanged(Vector3Int coordinates)
    {
        _dirty = true;
    }

    public void LateUpdateSingleton()
    {
        if (!_dirty)
        {
            return;
        }
        _dirty = false;
        foreach (var node in _highlighted)
        {
            _highlighter.UnhighlightSecondary(node);
        }
        _highlighted.Clear();
        if (_selected == null || !_selected)
        {
            return;
        }
        foreach (var station in _network.NetworkMembers())
        {
            // paused stations are off: DESIGN.md#pause
            if (station.IsPaused)
            {
                continue;
            }
            _highlighter.HighlightSecondary(station, _selected.HorizonColor);
            _highlighted.Add(station);
        }
    }
}
