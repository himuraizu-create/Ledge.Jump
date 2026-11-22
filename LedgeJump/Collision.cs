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
    };

    // Which filter the automatic edge detection uses. Set from the settings every frame.
    public static int Preset;

    public static unsafe bool Raycast(int preset, Vector3 origin, Vector3 direction, out RaycastHit hit,
        float maxDistance = 1000000f)
    {
        // Preset 0 goes through the game's own ready-made helper, exactly as before.
        if (preset <= 0 || preset >= Filters.Length)
            return BGCollisionModule.RaycastMaterialFilter(origin, direction, out hit, maxDistance);

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
        var ok = framework->BGCollisionModule->RaycastMaterialFilter(&result, &origin, &direction, maxDistance, f.LayerMask, flags);
        hit = result;
        return ok;
    }

    private static string F(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // Shoots rays straight down through (and around) the given position with every filter, and describes what each one found.
    public static unsafe List<string> Probe(Vector3 pos)
    {
        var lines = new List<string>();
        var down = new Vector3(0f, -1f, 0f);
        var centre = new Vector3(pos.X, pos.Y + 0.6f, pos.Z);

        for (var i = 0; i < Filters.Length; i++)
        {
            string under;
            if (Raycast(i, centre, down, out var hit))
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
                    if (Raycast(i, origin, down, out var h) && MathF.Abs(h.Point.Y - pos.Y) <= 0.3f)
                        seen++;
                }

                rings += $" {F(radius)}: {seen}/8";
            }

            lines.Add($"[{i}] {Filters[i].Name}: {under} | around you at your height:{rings}");
        }

        return lines;
    }
}
