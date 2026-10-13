using System.Collections.Generic;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using Timberborn.TemplateInstantiation;

namespace TeleporterStation;

// the ring's extract store, same pool as the station's: DESIGN.md#charges-and-the-charge-store
public class RingChargeStoreInitializer : IDedicatedDecoratorInitializer<RingSpec, Inventory>
{
    internal const int ChargeCapacity = TeleporterChargeStoreInitializer.ChargeCapacity;

    // persistence key; RingNode resolves the store by this name
    internal const string StoreComponentName = "RingCharges";

    private readonly InventoryInitializerFactory _inventoryInitializerFactory;

    public RingChargeStoreInitializer(InventoryInitializerFactory inventoryInitializerFactory)
    {
        _inventoryInitializerFactory = inventoryInitializerFactory;
    }

    public void Initialize(RingSpec subject, Inventory decorator)
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
