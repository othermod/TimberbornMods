using System.Collections.Generic;
using Timberborn.Persistence;
using Timberborn.SingletonSystem;
using Timberborn.WorldPersistence;
using Timberborn.WorldSerialization;
using Timberborn.ZiplineSystem;
using UnityEngine;

namespace TeleporterStation;

// pre-0.5 saves predate ZiplineTower on our prefabs; this injects the missing section before entities load: DESIGN.md#save-compatibility
public class TeleporterSaveCompat : ILoadableSingleton
{
    // the game's own key names inside ZiplineTower, pinned like the other internals: DESIGN.md#save-compatibility
    private static readonly ComponentKey ZiplineTowerKey = new ComponentKey("ZiplineTower");
    private static readonly ListKey<ZiplineTower> ConnectionTargetsKey = new ListKey<ZiplineTower>("ConnectionTargets");

    // pre-blob wormhole saves carry no marker section; the stable wormhole template names stand in: DESIGN.md#save-compatibility
    private static readonly HashSet<string> WormholeTemplates = new HashSet<string>
    {
        "Teleporter.IronTeeth",
        "Teleporter.Folktails",
    };

    private readonly ISerializedWorldSupplier _serializedWorldSupplier;
    private readonly ReferenceSerializer _referenceSerializer;

    public TeleporterSaveCompat(ISerializedWorldSupplier serializedWorldSupplier,
        ReferenceSerializer referenceSerializer)
    {
        _serializedWorldSupplier = serializedWorldSupplier;
        _referenceSerializer = referenceSerializer;
    }

    public void Load()
    {
        var world = _serializedWorldSupplier.Get();
        if (world == null)
        {
            Debug.LogWarning("[TeleporterStation] save compat: the save is not parsed yet; "
                + "pre-0.5 saves will fail to load");
            return;
        }
        var patched = 0;
        foreach (var entity in world.Entities())
        {
            if (entity.HasComponent(ZiplineTowerKey.Name))
            {
                continue;
            }
            if (!entity.HasComponent(RingNode.NodeKey.Name)
                && !entity.HasComponent(TeleporterNode.NodeKey.Name)
                && !WormholeTemplates.Contains(entity.TemplateName))
            {
                continue;
            }
            new EntitySaver(entity).GetComponent(ZiplineTowerKey)
                .Set(ConnectionTargetsKey, new List<ZiplineTower>(),
                    _referenceSerializer.Of<ZiplineTower>());
            patched++;
        }
        if (patched > 0)
        {
            Debug.Log($"[TeleporterStation] save compat: injected empty zipline state into {patched} pre-0.5 building(s)");
        }
    }
}
