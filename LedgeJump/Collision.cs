using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using ClientFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace LedgeJump;

// One way of asking the game's collision "what is there?"
// The game sorts every piece of collision by LAYER and by MATERIAL, and every ray question names which
// layers and materials it cares about. The usual ground check only asks about one layer and one material,
// so a platform that is filed under something else is invisible to it.
internal struct CollisionFilter
{
    public string Name;
    public string Help;
    public int LayerMask;          // collision layers to include (-1 = all of them)
    public ulong MaterialMask;     // material bits to look at
    public ulong MaterialValue;    // 0 = "any of those bits"; otherwise they must all match
}

internal static class Collision
{
    public static readonly CollisionFilter[] Filters =
    {
        new CollisionFilter
        {
            Name = "Standard",
            Help = "What Auto Edge has used until now: the game's usual ground check.",
            LayerMask = 1,
            MaterialMask = 0x4000,
            MaterialValue = 0x4000,
        },
        new CollisionFilter
        {
            Name = "All layers",
            Help = "Same material as Standard, but includes collision filed under any layer.",
            LayerMask = -1,
            MaterialMask = 0x4000,
            MaterialValue = 0x4000,
        },
        new CollisionFilter
        {
            Name = "Any material",
            Help = "Only the standard layer, but any kind of surface material.",
            LayerMask = 1,
            MaterialMask = ulong.MaxValue,
            MaterialValue = 0,
        },
        new CollisionFilter
        {
            Name = "Everything",
            Help = "Any layer and any material. Sees the most, but can also see things that are not floors.",
            LayerMask = -1,
            MaterialMask = ulong.MaxValue,
            MaterialValue = 0,
        },
        new CollisionFilter
        {
            Name = "Standard + Everything (recommended)",
            Help = "Asks both Standard and Everything, and uses whichever finds something closer.\nSees ordinary ground the way Standard does, and the odd objects Everything finds.",
        },
    };

    // Index of the combined entry above.
    public const int CombinedPreset = 4;

    // Which filter the automatic edge detection uses, and whether it sweeps a body-sized sphere instead of a thin ray.
    // Both are set from the settings every frame.
    public static int Preset;
    public static bool Sweep;

    // sweep = false: a thin ray, like a laser pointer.
    // sweep = true: a ball of the game's own sweep size travels along the same line, so it also "feels" geometry
    //               slightly to the side, a bit like a body does.
    public static unsafe bool Raycast(int preset, bool sweep, Vector3 origin, Vector3 direction, out RaycastHit hit,
        float maxDistance = 1000000f)
    {
        // Combined: ask Standard and Everything, keep whichever hit is closer to where the ray started.
        if (preset == CombinedPreset)
        {
            var okStandard = Raycast(0, sweep, origin, direction, out var hitStandard, maxDistance);
            var okEverything = Raycast(3, sweep, origin, direction, out var hitEverything, maxDistance);

            if (okStandard && okEverything)
            {
                hit = (hitStandard.Point - origin).LengthSquared() <= (hitEverything.Point - origin).LengthSquared()
                    ? hitStandard
                    : hitEverything;
                return true;
            }

            if (okStandard)
            {
                hit = hitStandard;
                return true;
            }

            if (okEverything)
            {
                hit = hitEverything;
                return true;
            }

            hit = default;
            return false;
        }

        // Preset 0 goes through the game's own ready-made helpers, exactly as before.
        if (preset <= 0 || preset >= Filters.Length)
        {
            return sweep
                ? BGCollisionModule.SweepSphereMaterialFilter(origin, direction, out hit, maxDistance)
                : BGCollisionModule.RaycastMaterialFilter(origin, direction, out hit, maxDistance);
        }

        var framework = ClientFramework.Instance();
        if (framework == null || framework->BGCollisionModule == null)
        {
            hit = default;
            return false;
        }

        var f = Filters[preset];
        var flags = stackalloc int[4];
        flags[0] = unchecked((int)(uint)(f.MaterialMask & 0xFFFFFFFFUL));
        flags[1] = unchecked((int)(uint)(f.MaterialMask >> 32));
        flags[2] = unchecked((int)(uint)(f.MaterialValue & 0xFFFFFFFFUL));
        flags[3] = unchecked((int)(uint)(f.MaterialValue >> 32));

        var result = new RaycastHit();
        var ok = sweep
            ? framework->BGCollisionModule->SweepSphereMaterialFilter(&result, &origin, &direction, maxDistance, f.LayerMask, flags)
            : framework->BGCollisionModule->RaycastMaterialFilter(&result, &origin, &direction, maxDistance, f.LayerMask, flags);
        hit = result;
        return ok;
    }

    private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // Shoots rays straight down through (and around) the given position with every filter, as a thin ray and as a
    // body-sized sweep, and describes what each one found.
    public static unsafe List<string> Probe(Vector3 pos)
    {
        var lines = new List<string>();
        var down = new Vector3(0f, -1f, 0f);
        var centre = new Vector3(pos.X, pos.Y + 0.6f, pos.Z);

        for (var i = 0; i < Filters.Length; i++)
        {
            foreach (var sweep in new[] { false })
            {
                string under;
                if (Raycast(i, sweep, centre, down, out var hit))
                {
                    under = $"HIT {F(hit.Point.Y - pos.Y)} from your feet";
                    under += $" | material 0x{hit.Material:X}";

                    var collider = hit.Object;
                    if (collider != null)
                        under += $" | layer 0x{collider->LayerMask:X} | {collider->GetColliderType()}";

                    var n = Vector3.Cross(hit.V2 - hit.V1, hit.V3 - hit.V1);
                    var len = n.Length();
                    if (len > 0.00001f)
                        under += $" | flatness {F(MathF.Abs(n.Y) / len)}";
                }
                else
                {
                    under = "nothing under you";
                }

                // How much of the ground right around you does this filter see at your height?
                var rings = "";
                foreach (var radius in new[] { 0.25f, 0.5f })
                {
                    var seen = 0;
                    for (var k = 0; k < 8; k++)
                    {
                        var a = k * MathF.PI / 4f;
                        var origin = new Vector3(pos.X + MathF.Cos(a) * radius, pos.Y + 0.6f, pos.Z + MathF.Sin(a) * radius);
                        if (Raycast(i, sweep, origin, down, out var h) && MathF.Abs(h.Point.Y - pos.Y) <= 0.3f)
                            seen++;
                    }

                    rings += $" {F(radius)}: {seen}/8";
                }

                lines.Add($"[{i}] {Filters[i].Name} ({(sweep ? "sweep" : "ray")}): {under} | around you at your height:{rings}");
            }
        }

        return lines;
    }
}
