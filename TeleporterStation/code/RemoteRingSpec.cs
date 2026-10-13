using Timberborn.BlueprintSystem;

namespace TeleporterStation;

// marker spec of the free half of a pair: DESIGN.md#ring-pairs
public record RemoteRingSpec : ComponentSpec
{
    // tunnel prefix dodges the achievements' structure blacklist: DESIGN.md#ring-pairs
    internal const string TemplateNamePrefix = "Tunnel.TeleporterRingRemote.";
}
