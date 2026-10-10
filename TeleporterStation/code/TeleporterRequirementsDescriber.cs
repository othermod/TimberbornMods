using System.Collections.Generic;
using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.CoreUI;
using Timberborn.EntityPanelSystem;
using Timberborn.Localization;

namespace TeleporterStation;

// design: DESIGN.md#entity-panel
public class TeleporterRequirementsDescriber : BaseComponent, IAwakableComponent,
    IEntityDescriber
{
    private const string NeedsHaulersLocKey = "GoodConsuming.NeedsHaulers";

    private readonly ILoc _loc;
    private BlockObject _blockObject;

    public TeleporterRequirementsDescriber(ILoc loc)
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
                SpecialStrings.RowStarter + _loc.T(NeedsHaulersLocKey), 2030);
        }
    }
}
