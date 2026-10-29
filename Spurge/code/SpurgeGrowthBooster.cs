using Timberborn.BaseComponentSystem;
using Timberborn.BlockSystem;
using Timberborn.EntitySystem;
using Timberborn.Gathering;
using Timberborn.Growing;
using Timberborn.NaturalResourcesLifecycle;
using Timberborn.SoilContaminationSystem;
using Timberborn.TimeSystem;
using Timberborn.WaterSystem;
using UnityEngine;

namespace Spurge;

// design: DESIGN.md#growth-booster
public class SpurgeGrowthBooster : BaseComponent, IAwakableComponent, IInitializableEntity, IDeletableEntity
{
    // the contamination level the game's own soil simulator treats as a badwater source: DESIGN.md#tiers
    private const float BadwaterContamination = 0.5f;

    // design: DESIGN.md#growth-booster
    private const float TickIntervalInDays = 0.25f;

    private readonly ITimeTriggerFactory _timeTriggerFactory;
    private readonly IThreadSafeWaterMap _waterMap;
    private readonly ISoilContaminationService _soilContaminationService;

    private BlockObject _blockObject;
    private SpurgeSpec _spec;
    private Gatherable _gatherable;
    private GatherableYieldGrower _grower;
    private Growable _growable;
    private LivingNaturalResource _livingNaturalResource;
    private DyingNaturalResource _dyingNaturalResource;
    private ITimeTrigger _tick;

    public float CurrentSpeedMultiplier { get; private set; } = 1f;

    public SpurgeGrowthBooster(ITimeTriggerFactory timeTriggerFactory,
                               IThreadSafeWaterMap waterMap,
                               ISoilContaminationService soilContaminationService)
    {
        _timeTriggerFactory = timeTriggerFactory;
        _waterMap = waterMap;
        _soilContaminationService = soilContaminationService;
    }

    public void Awake()
    {
        _blockObject = GetComponent<BlockObject>();
        _spec = GetComponent<SpurgeSpec>();
        _gatherable = GetComponent<Gatherable>();
        _grower = GetComponent<GatherableYieldGrower>();
        _growable = GetComponent<Growable>();
        _livingNaturalResource = GetComponent<LivingNaturalResource>();
        _dyingNaturalResource = GetComponent<DyingNaturalResource>();
        _tick = _timeTriggerFactory.Create(Tick, TickIntervalInDays);
    }

    public void DeleteEntity()
    {
        _tick.Reset();
    }

    // a TimeTrigger created in Awake never fires until resumed; vanilla resumes in InitializeEntity
    public void InitializeEntity()
    {
        _tick.Resume();
    }

    private void Tick()
    {
        _tick.Reset();
        _tick.Resume();
        RefreshEnvironment();
        if (_blockObject.IsPreview || !_growable.IsGrown || _livingNaturalResource.IsDead
            || _dyingNaturalResource.IsDying || _gatherable.Yielder.IsYielding
            || !_gatherable.UsableWithCurrentFeatureToggles)
        {
            return;
        }
        float speedup = CurrentSpeedMultiplier - 1f;
        if (speedup <= 0f)
        {
            return;
        }
        // progress is normalized over the window: convert the quarter-day boost to a fraction
        float delta = TickIntervalInDays * speedup / _gatherable.YieldGrowthTimeInDays;
        _grower.FastForwardGrowth(delta);
    }

    public float BaselineDays()
    {
        return _gatherable.YieldGrowthTimeInDays;
    }

    // tier order and the badwater line: DESIGN.md#tiers
    private void RefreshEnvironment()
    {
        Vector3Int baseCoordinates = _blockObject.CoordinatesAtBaseZ;
        if (_waterMap.CeiledWaterHeight(baseCoordinates) > baseCoordinates.z
            && _waterMap.ColumnContamination(baseCoordinates) >= BadwaterContamination)
        {
            CurrentSpeedMultiplier = _spec.BadwaterSpeedMultiplier;
        }
        else if (_soilContaminationService.SoilIsContaminated(baseCoordinates))
        {
            CurrentSpeedMultiplier = _spec.ContaminatedSoilSpeedMultiplier;
        }
        else
        {
            CurrentSpeedMultiplier = 1f;
        }
    }
}
