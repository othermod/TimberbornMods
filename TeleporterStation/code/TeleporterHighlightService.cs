using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.GameDistricts;
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
    private readonly RingNetwork _rings;

    private readonly HashSet<BaseComponent> _highlighted = new();
    private TeleporterNode _selected;
    private RingNode _selectedRing;
    private bool _dirty;

    public TeleporterHighlightService(Highlighter highlighter, EventBus eventBus,
        EntitySelectionService entitySelectionService, TeleporterNetwork network,
        RingNetwork rings)
    {
        _highlighter = highlighter;
        _eventBus = eventBus;
        _entitySelectionService = entitySelectionService;
        _network = network;
        _rings = rings;
    }

    public void Load()
    {
        _eventBus.Register(this);
        _network.PairingChanged += OnPairingChanged;
        _network.StationPauseChanged += OnStationPauseChanged;
        _rings.RingStateChanged += OnRingStateChanged;
    }

    [OnEvent]
    public void OnSelectableObjectSelected(SelectableObjectSelectedEvent selectedEvent)
    {
        var selected = _entitySelectionService.SelectedObject;
        _selected = selected.GetComponent<TeleporterNode>();
        _selectedRing = selected.GetComponent<RingNode>();
        _dirty = true;
    }

    [OnEvent]
    public void OnSelectableObjectUnselected(SelectableObjectUnselectedEvent unselectedEvent)
    {
        _selected = null;
        _selectedRing = null;
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

    private void OnRingStateChanged(Vector3Int coordinates)
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
        foreach (var highlighted in _highlighted)
        {
            _highlighter.UnhighlightSecondary(highlighted);
        }
        _highlighted.Clear();
        if (_selected != null && _selected)
        {
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
            return;
        }
        HighlightRingPair();
    }

    // the pair's hue is its built half's district; the remote sits beyond paths and carries no district: DESIGN.md#selection-highlight
    private void HighlightRingPair()
    {
        if (_selectedRing == null || !_selectedRing || !_selectedRing.IsPaired)
        {
            return;
        }
        var built = _selectedRing.IsRemote ? _selectedRing.Partner : _selectedRing;
        var center = built.GetComponent<DistrictBuilding>()?.District;
        if (center?.District == null)
        {
            return;
        }
        var color = TeleporterNode.DistrictColor(center.District.CenterNodeId);
        foreach (var ring in new[] { _selectedRing, _selectedRing.Partner })
        {
            _highlighter.HighlightSecondary(ring, color);
            _highlighted.Add(ring);
        }
    }
}
