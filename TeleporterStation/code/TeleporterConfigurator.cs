using Bindito.Core;
using Timberborn.BlockSystem;
using Timberborn.Characters;
using Timberborn.Emptying;
using Timberborn.EntityPanelSystem;
using Timberborn.InventorySystem;
using Timberborn.TemplateInstantiation;
using Timberborn.Workshops;

namespace TeleporterStation;

[Context("Game")]
// design: DESIGN.md#component-wiring
public class TeleporterConfigurator : Configurator
{
    public override void Configure()
    {
        Bind<TeleporterNetwork>().AsSingleton();
        Bind<TeleporterHighlightService>().AsSingleton();
        Bind<TeleporterNode>().AsTransient();
        Bind<TeleporterTransitTracker>().AsTransient();
        Bind<OutOfExtractStatus>().AsTransient();
        Bind<TeleporterConnectivityStatus>().AsTransient();
        Bind<TeleporterRequirementsDescriber>().AsTransient();
        Bind<TeleporterFragment>().AsSingleton();
        // Collected into the game's BlockObjectValidationService alongside
        // its own rules: refuses a teleporter ghost on any district but
        // the one whose paths already reach a teleporter (the
        // single-district rule; see TeleporterPlacementValidator).
        MultiBind<IBlockObjectValidator>().To<TeleporterPlacementValidator>().AsSingleton();
        MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
        MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
    }

    // design: DESIGN.md#component-wiring
    private class TemplateModuleProvider : IProvider<TemplateModule>
    {
        private readonly TeleporterChargeStoreInitializer _chargeStoreInitializer;

        public TemplateModuleProvider(InventoryInitializerFactory inventoryInitializerFactory)
        {
            _chargeStoreInitializer = new TeleporterChargeStoreInitializer(inventoryInitializerFactory);
        }

        public TemplateModule Get()
        {
            var builder = new TemplateModule.Builder();
            builder.AddDecorator<TeleporterSpec, TeleporterNode>();
            // The vanilla status plumbing (StatusSubject + StatusIconCycler
            // arrive with every BuildingSpec) needs a publisher component
            // for the dry-store icon; mirrors the game's
            // LackOfResourcesStatus on workplaces.
            builder.AddDecorator<TeleporterSpec, OutOfExtractStatus>();
            builder.AddDecorator<TeleporterSpec, TeleporterConnectivityStatus>();
            builder.AddDecorator<TeleporterSpec, TeleporterRequirementsDescriber>();
            builder.AddDecorator<Character, TeleporterTransitTracker>();
            builder.AddDedicatedDecorator(_chargeStoreInitializer);
            builder.AddDecorator<TeleporterSpec, Emptiable>();
            builder.AddDecorator<TeleporterSpec, EmptyInventoriesWorkplaceBehavior>();
            builder.AddDecorator<TeleporterSpec, RemoveUnwantedStockWorkplaceBehavior>();
            builder.AddDecorator<TeleporterSpec, FillInputHaulBehaviorProvider>();
            builder.AddDecorator<TeleporterSpec, FillInputWorkplaceBehavior>();
            return builder.Build();
        }
    }

    private class EntityPanelModuleProvider : IProvider<EntityPanelModule>
    {
        private readonly TeleporterFragment _fragment;

        public EntityPanelModuleProvider(TeleporterFragment fragment)
        {
            _fragment = fragment;
        }

        public EntityPanelModule Get()
        {
            var builder = new EntityPanelModule.Builder();
            builder.AddBottomFragment(_fragment, 10);
            return builder.Build();
        }
    }
}
