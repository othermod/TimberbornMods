using Bindito.Core;
using Timberborn.BlockSystem;
using Timberborn.BlockSystemUI;
using Timberborn.Characters;
using Timberborn.Emptying;
using Timberborn.EntityPanelSystem;
using Timberborn.InventorySystem;
using Timberborn.Navigation;
using Timberborn.TemplateInstantiation;
using Timberborn.Workshops;

namespace TeleporterStation;

[Context("Game")]
// design: DESIGN.md#component-wiring, DESIGN.md#ring-pairs
public class TeleporterConfigurator : Configurator
{
    public override void Configure()
    {
        Bind<TeleporterNetwork>().AsSingleton();
        Bind<RingNetwork>().AsSingleton();
        Bind<RingPairingState>().AsSingleton();
        Bind<TeleporterHighlightService>().AsSingleton();
        Bind<RingPairingFlow>().AsSingleton();
        Bind<RingConnectionLine>().AsSingleton();
        Bind<TeleporterNode>().AsTransient();
        Bind<RingNode>().AsTransient();
        Bind<RingPausableMarker>().AsTransient();
        Bind<TeleporterTransitTracker>().AsTransient();
        Bind<RingTransitVisual>().AsTransient();
        Bind<OutOfExtractStatus>().AsTransient();
        Bind<TeleporterConnectivityStatus>().AsTransient();
        Bind<TeleporterRequirementsDescriber>().AsTransient();
        Bind<RingDryStatus>().AsTransient();
        Bind<RingUnpairedStatus>().AsTransient();
        Bind<RingConnectivityBlocker>().AsTransient();
        Bind<RingRequirementsDescriber>().AsTransient();
        Bind<TeleporterFragment>().AsSingleton();
        Bind<RingFragment>().AsSingleton();
        // the mod's placement rules join the game's own pipeline: DESIGN.md#one-network, DESIGN.md#ring-pairs
        MultiBind<IBlockObjectValidator>().To<TeleporterPlacementValidator>().AsSingleton();
        MultiBind<IBlockObjectValidator>().To<RingPlacementValidator>().AsSingleton();
        // every crossing, station or ring, joins the game's path transformers: DESIGN.md#travel-timed-crossings
        MultiBind<IPathTransformer>().To<TeleporterPathTransformer>().AsSingleton();
        MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
        MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
    }

    // design: DESIGN.md#component-wiring, DESIGN.md#ring-pairs
    private class TemplateModuleProvider : IProvider<TemplateModule>
    {
        private readonly TeleporterChargeStoreInitializer _chargeStoreInitializer;
        private readonly RingChargeStoreInitializer _ringChargeStoreInitializer;

        public TemplateModuleProvider(InventoryInitializerFactory inventoryInitializerFactory)
        {
            _chargeStoreInitializer = new TeleporterChargeStoreInitializer(inventoryInitializerFactory);
            _ringChargeStoreInitializer = new RingChargeStoreInitializer(inventoryInitializerFactory);
        }

        public TemplateModule Get()
        {
            var builder = new TemplateModule.Builder();
            builder.AddDecorator<TeleporterSpec, TeleporterNode>();
            // dry-store icon publisher, the game's own status plumbing: DESIGN.md#statuses-and-vanilla-icon-suppression
            builder.AddDecorator<TeleporterSpec, OutOfExtractStatus>();
            builder.AddDecorator<TeleporterSpec, TeleporterConnectivityStatus>();
            builder.AddDecorator<TeleporterSpec, TeleporterRequirementsDescriber>();
            builder.AddDecorator<Character, TeleporterTransitTracker>();
            // the wormhole traveller visual rides every character: DESIGN.md#ring-transit
            builder.AddDecorator<Character, RingTransitVisual>();
            builder.AddDedicatedDecorator(_chargeStoreInitializer);
            builder.AddDecorator<TeleporterSpec, Emptiable>();
            builder.AddDecorator<TeleporterSpec, EmptyInventoriesWorkplaceBehavior>();
            builder.AddDecorator<TeleporterSpec, RemoveUnwantedStockWorkplaceBehavior>();
            builder.AddDecorator<TeleporterSpec, FillInputHaulBehaviorProvider>();
            builder.AddDecorator<TeleporterSpec, FillInputWorkplaceBehavior>();
            // the rings: node and statuses on both markers, store plumbing on the built half
            builder.AddDecorator<RingSpec, RingNode>();
            builder.AddDecorator<RemoteRingSpec, RingNode>();
            // pause marker on the built half only; the remote is never pausable: DESIGN.md#pause
            builder.AddDecorator<RingSpec, RingPausableMarker>();
            builder.AddDecorator<RingSpec, RingDryStatus>();
            builder.AddDecorator<RemoteRingSpec, RingDryStatus>();
            builder.AddDecorator<RingSpec, RingUnpairedStatus>();
            builder.AddDecorator<RemoteRingSpec, RingUnpairedStatus>();
            builder.AddDecorator<RingSpec, RingConnectivityBlocker>();
            builder.AddDecorator<RemoteRingSpec, RingConnectivityBlocker>();
            builder.AddDecorator<RingSpec, RingRequirementsDescriber>();
            // remote has no placement spec: the game's spec-keyed panel describers re-key on the marker: DESIGN.md#ring-pairs
            builder.AddDecorator<RemoteRingSpec, PlaceableBlockObjectDescriber>();
            builder.AddDecorator<RemoteRingSpec, LabeledEntityBadge>();
            builder.AddDedicatedDecorator(_ringChargeStoreInitializer);
            builder.AddDecorator<RingSpec, Emptiable>();
            builder.AddDecorator<RingSpec, EmptyInventoriesWorkplaceBehavior>();
            builder.AddDecorator<RingSpec, RemoveUnwantedStockWorkplaceBehavior>();
            builder.AddDecorator<RingSpec, FillInputHaulBehaviorProvider>();
            builder.AddDecorator<RingSpec, FillInputWorkplaceBehavior>();
            return builder.Build();
        }
    }

    private class EntityPanelModuleProvider : IProvider<EntityPanelModule>
    {
        private readonly TeleporterFragment _fragment;
        private readonly RingFragment _ringFragment;

        public EntityPanelModuleProvider(TeleporterFragment fragment, RingFragment ringFragment)
        {
            _fragment = fragment;
            _ringFragment = ringFragment;
        }

        public EntityPanelModule Get()
        {
            var builder = new EntityPanelModule.Builder();
            builder.AddBottomFragment(_fragment, 10);
            builder.AddBottomFragment(_ringFragment, 11);
            return builder.Build();
        }
    }
}
