using System;
using System.Reflection;
using Timberborn.BlockSystem;
using Timberborn.ZiplineSystem;
using UnityEngine;

namespace TeleporterStation;

// the ghost poses as a connectable zipline origin; a private spec copy with MaxDistance 0 fails the candidate query's distance test: DESIGN.md#ring-pairs
internal static class ZiplinePreviewVeil
{
    private static readonly FieldInfo SpecField = typeof(ZiplineTower).GetField("_ziplineTowerSpec",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Type SpecType = SpecField.FieldType;
    private static readonly PropertyInfo MaxDistance = SpecType.GetProperty("MaxDistance");
    private static readonly PropertyInfo CableAnchorPoint = SpecType.GetProperty("CableAnchorPoint");
    private static readonly PropertyInfo MaxConnections = SpecType.GetProperty("MaxConnections");
    private static readonly PropertyInfo UnobstructedCoordinates = SpecType.GetProperty("UnobstructedCoordinates");

    // idempotent and instance-local; the blueprint's shared spec instance is never touched: DESIGN.md#ring-pairs
    internal static void Silence(BlockObject blockObject)
    {
        if (!blockObject.IsPreview)
        {
            return;
        }
        var tower = blockObject.GetComponent<ZiplineTower>();
        if (tower == null)
        {
            return;
        }
        var spec = SpecField.GetValue(tower);
        if (spec == null || (int)MaxDistance.GetValue(spec) == 0)
        {
            return;
        }
        var silent = Activator.CreateInstance(SpecType);
        CableAnchorPoint.SetValue(silent, CableAnchorPoint.GetValue(spec));
        MaxConnections.SetValue(silent, MaxConnections.GetValue(spec));
        MaxDistance.SetValue(silent, 0);
        UnobstructedCoordinates.SetValue(silent, UnobstructedCoordinates.GetValue(spec));
        SpecField.SetValue(tower, silent);
    }
}
