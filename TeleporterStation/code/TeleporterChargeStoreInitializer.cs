using System.Collections.Generic;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.TemplateInstantiation;

namespace TeleporterStation;

// design: DESIGN.md#charges-and-the-charge-store
public class TeleporterChargeStoreInitializer : IDedicatedDecoratorInitializer<TeleporterSpec, Inventory>
{
    internal const int ChargeCapacity = 25;

    // persistence key; TeleporterNode resolves the store by this name
    internal const string StoreComponentName = "TeleporterCharges";

    private readonly InventoryInitializerFactory _inventoryInitializerFactory;

    public TeleporterChargeStoreInitializer(InventoryInitializerFactory inventoryInitializerFactory)
    {
        _inventoryInitializerFactory = inventoryInitializerFactory;
    }

    public void Initialize(TeleporterSpec subject, Inventory decorator)
    {
        var goods = new List<StorableGoodAmount>
        {
            new(StorableGood.CreateAsGivable(TeleporterNode.ExtractGoodId), ChargeCapacity)
        };
        var initializer = _inventoryInitializerFactory.Create(decorator, ChargeCapacity, StoreComponentName);
        initializer.AddAllowedGoods(goods);
        initializer.Initialize();
    }
}
