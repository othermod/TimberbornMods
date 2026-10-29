using Bindito.Core;
using Timberborn.EntityPanelSystem;
using Timberborn.TemplateInstantiation;

namespace Spurge;

// design: DESIGN.md#component-wiring
[Context("Game")]
public class SpurgeConfigurator : Configurator
{
    public override void Configure()
    {
        Bind<SpurgeGrowthBooster>().AsTransient();
        Bind<SpurgeFragment>().AsSingleton();
        MultiBind<TemplateModule>().ToProvider<TemplateModuleProvider>().AsSingleton();
        MultiBind<EntityPanelModule>().ToProvider<EntityPanelModuleProvider>().AsSingleton();
    }

    // design: DESIGN.md#component-wiring
    private class TemplateModuleProvider : IProvider<TemplateModule>
    {
        public TemplateModule Get()
        {
            var builder = new TemplateModule.Builder();
            builder.AddDecorator<SpurgeSpec, SpurgeGrowthBooster>();
            return builder.Build();
        }
    }

    private class EntityPanelModuleProvider : IProvider<EntityPanelModule>
    {
        private readonly SpurgeFragment _fragment;

        public EntityPanelModuleProvider(SpurgeFragment fragment)
        {
            _fragment = fragment;
        }

        public EntityPanelModule Get()
        {
            var builder = new EntityPanelModule.Builder();
            // order 1 keeps this fragment after vanilla's middle fragments in the per-frame pass: docs/entity-panel-ui.md
            builder.AddMiddleFragment(_fragment, 1);
            return builder.Build();
        }
    }
}
