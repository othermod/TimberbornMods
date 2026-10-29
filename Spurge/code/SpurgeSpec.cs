using Timberborn.BlueprintSystem;

namespace Spurge;

// design: DESIGN.md#growth-booster
public record SpurgeSpec : ComponentSpec
{
    [Serialize]
    public float ContaminatedSoilSpeedMultiplier { get; init; }

    [Serialize]
    public float BadwaterSpeedMultiplier { get; init; }
}
