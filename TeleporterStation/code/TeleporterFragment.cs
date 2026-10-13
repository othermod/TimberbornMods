using System.Linq;
using System.Text;
using Timberborn.AssetSystem;
using Timberborn.BaseComponentSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.InventorySystemUI;
using Timberborn.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace TeleporterStation;

// design: DESIGN.md#entity-panel
public class TeleporterFragment : IEntityPanelFragment
{
    private const string RowTemplatePath = "Game/EntityPanel/InventoryInformationalRow";
    private const string OutputTypeClass = "inventory-row-informational__type--output";
    private const string IconPath = "Buildings/Paths/Teleporter/TeleporterIcon";

    private readonly TeleporterNetwork _network;
    private readonly ILoc _loc;
    private readonly InformationalRowsFactory _rowsFactory;
    private readonly VisualElementLoader _visualElementLoader;
    private readonly IAssetLoader _assetLoader;

    private VisualElement _root;
    private VisualElement _mainSection;
    private VisualElement _buttonSection;
    private VisualElement _rows;
    private Label _hint;
    private VisualElement _buttons;
    private InformationalRow _extractRow;
    private InformationalRow _transitRow;
    private Inventory _boundStore;
    private TeleporterNode _node;
    private string _signature;
    private string _lastSeenSignature;

    public TeleporterFragment(TeleporterNetwork network,
                              ILoc loc,
                              InformationalRowsFactory rowsFactory,
                              VisualElementLoader visualElementLoader,
                              IAssetLoader assetLoader)
    {
        _network = network;
        _loc = loc;
        _rowsFactory = rowsFactory;
        _visualElementLoader = visualElementLoader;
        _assetLoader = assetLoader;
    }

    public VisualElement InitializeFragment()
    {
        _root = new VisualElement();

        _mainSection = new NineSliceVisualElement();
        _mainSection.AddToClassList("entity-sub-panel");
        _mainSection.AddToClassList("bg-sub-box--green");
        _root.Add(_mainSection);

        _rows = new VisualElement();
        _mainSection.Add(_rows);

        _buttonSection = new NineSliceVisualElement();
        _buttonSection.AddToClassList("entity-sub-panel");
        _buttonSection.AddToClassList("bg-sub-box--green");
        _root.Add(_buttonSection);
        _buttonSection.ToggleDisplayStyle(visible: false);

        _hint = new Label { style = { whiteSpace = WhiteSpace.Normal } };
        _hint.AddToClassList("entity-panel__text");
        _buttonSection.Add(_hint);

        _buttons = new VisualElement { style = { flexDirection = FlexDirection.Column } };
        _buttonSection.Add(_buttons);

        _root.ToggleDisplayStyle(visible: false);
        return _root;
    }

    public void ShowFragment(BaseComponent entity)
    {
        _node = entity.GetComponent<TeleporterNode>();
        _lastSeenSignature = null;
        _root.ToggleDisplayStyle(_node != null && _node.IsFinished);
        _signature = null;
        RebuildRows();
        if (_node == null)
        {
            SetHint(string.Empty);
            _buttons.Clear();
            _buttonSection.ToggleDisplayStyle(visible: false);
        }
    }

    public void ClearFragment()
    {
        _node = null;
        _signature = null;
        _lastSeenSignature = null;
        RebuildRows();
        _root.ToggleDisplayStyle(visible: false);
    }

    public void UpdateFragment()
    {
        if (_node == null)
        {
            return;
        }
        if (_node.IsFinished && _node.ChargeStore != _boundStore)
        {
            RebuildRows();
        }
        _extractRow?.ShowUpdated();
        _transitRow?.ShowUpdated();

        var finished = _node.IsFinished;
        var charged = finished && _node.IsCharged;
        // only a network member on real paths can serve: DESIGN.md#wormhole-siphon
        var canServe = finished && _network.CanServeAsSource(_node.Coordinates);
        var networkCount = _network.NetworkMembers().Count();

        var signature = new StringBuilder()
            .Append(finished)
            .Append('|').Append(canServe)
            .Append('|').Append(charged)
            .Append('|').Append(networkCount)
            .Append('|').Append(_node.IsExtractSource)
            .ToString();
        if (signature == _signature)
        {
            return;
        }
        // one-frame settle: DESIGN.md#entity-panel
        if (signature != _lastSeenSignature)
        {
            _lastSeenSignature = signature;
            return;
        }
        _signature = signature;
        _buttons.Clear();

        if (!finished)
        {
            _root.ToggleDisplayStyle(visible: false);
            return;
        }
        _root.ToggleDisplayStyle(visible: true);
        SetHint(_loc.T("Teleporter.NetworkCount", networkCount));
        // the extract-source toggle is the only station action: DESIGN.md#wormhole-siphon
        if (canServe)
        {
            _buttons.Add(StyledButton(
                _loc.T(_node.IsExtractSource
                    ? "Teleporter.Source.Disable"
                    : "Teleporter.Source.Enable"),
                () => _node.ToggleExtractSource()));
        }
        _buttonSection.ToggleDisplayStyle(_buttons.childCount > 0
            || _hint.text.Length > 0);
    }

    private void SetHint(string text)
    {
        _hint.text = text;
        _hint.ToggleDisplayStyle(text.Length > 0);
    }

    private void RebuildRows()
    {
        _rows.Clear();
        _extractRow = null;
        _transitRow = null;
        _boundStore = null;
        if (_node == null || !_node.IsFinished)
        {
            return;
        }
        var store = _node.ChargeStore;
        if (store == null)
        {
            return;
        }
        _boundStore = store;
        _extractRow = _rowsFactory.CreateInputRowWithLimit(
            StorableGood.CreateAsGivable(TeleporterNode.ExtractGoodId), store, _rows);
        _transitRow = BuildTransitRow();
    }

    private InformationalRow BuildTransitRow()
    {
        var root = _visualElementLoader.LoadVisualElement(RowTemplatePath);
        root.Q("Type").AddToClassList(OutputTypeClass);
        root.Q<Image>("Image").sprite = _assetLoader.Load<Sprite>(IconPath);
        root.Q<Label>("Name").text = _loc.T("Teleporter.Transits");
        // hand-built rows attach their own root: DESIGN.md#entity-panel
        _rows.Add(root);
        return new InformationalRow(
            "TeleporterTransits",
            root,
            root.Q<Label>("Amount"),
            () => _node != null && _node.IsFinished
                ? System.Math.Max(0, _node.TransitsRemaining) : 0,
            showLimit: true,
            () => TeleporterChargeStoreInitializer.ChargeCapacity * TeleporterNode.CrossingsPerCharge,
            root.Q<Label>("Limit"),
            root.Q<Label>("Separator"));
    }

    // vanilla Automate button recipe: docs/game-ui-reference.md
    private static VisualElement StyledButton(string text, System.Action clicked, bool enabled = true)
    {
        var wrapper = new VisualElement();
        wrapper.AddToClassList("entity-panel__button-wrapper");
        wrapper.AddToClassList("transmitter-selector--automatable-none");
        var button = new NineSliceButton { text = text };
        button.AddToClassList("dropdown__selection");
        button.AddToClassList("dropdown__selectable");
        button.AddToClassList("entity-panel__text");
        button.style.height = 28f;
        button.clicked += clicked;
        button.SetEnabled(enabled);
        wrapper.Add(button);
        return wrapper;
    }
}
