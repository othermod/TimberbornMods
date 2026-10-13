using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.Localization;

namespace TeleporterStation;

// build-menu bullet, the game's own describer pipeline: DESIGN.md#entity-panel
public class RingRequirementsDescriber : BaseComponent, IAwakableComponent,
    IEntityDescriber
{
    private const string LocKey = "Ring.Requirements";
    // the vanilla haulage line, the same key the station's describer uses: DESIGN.md#entity-panel
    private const string NeedsHaulersLocKey = "GoodConsuming.NeedsHaulers";

    private readonly ILoc _loc;
    private BlockObject _blockObject;

    public RingRequirementsDescriber(ILoc loc)
    {
        _loc = loc;
    }

    public void Awake()
    {
        _blockObject = GetComponent<BlockObject>();
    }

    public IEnumerable<EntityDescription> DescribeEntity()
    {
        if (_blockObject.IsPreview)
        {
            yield return EntityDescription.CreateTextSection(
                SpecialStrings.RowStarter + _loc.T(LocKey), 2030);
            yield return EntityDescription.CreateTextSection(
                SpecialStrings.RowStarter + _loc.T(NeedsHaulersLocKey), 2031);
        }
    }
}
