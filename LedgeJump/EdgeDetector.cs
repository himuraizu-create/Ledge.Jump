using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace LedgeJump;

internal struct EdgeResult
{
    // Standing on something the game's collision can see?
    public bool Grounded;
    public float GroundY;

    // Was an edge (a drop) found ahead of you, and how far ahead (yalms)?
    public bool HasEdge;
    public float EdgeDistance;

    // Is a wall in the way before that edge? (then it is not a ledge, so no jump)
    public bool WallAhead;

    // Only filled in when Grounded is false: height difference to the closest ground the checks found
    // (positive = ground is below you). NaN = no collision was found at all.
    public float NearestGroundBelow;
}

// Looks at the game's real collision geometry by shooting invisible rays at it.
// Must only be called from the game's main thread (the plugin's per-frame update does that).
internal sealed class EdgeDetector
{
    private const float RayLift = 0.6f;     // start downward rays this far above the sample height
    private const float CoarseStep = 0.1f;  // how finely to sample along the walking direction
    private const int RefineSteps = 4;      // halvings used to pin down the exact edge position (0.1 / 16 = about 0.006)
    private const float WallHeight = 0.8f;  // height above the ground of the wall-check ray

    private readonly Configuration config;

    public EdgeDetector(Configuration config)
    {
        this.config = config;
    }

    // Shoots a ray straight down from just above (x, fromY, z). Returns the height of the first surface it hits.
    private static unsafe bool CastDown(float x, float fromY, float z, out float hitY)
    {
        var origin = new Vector3(x, fromY + RayLift, z);
        if (Collision.Raycast(Collision.Preset, Collision.Sweep, origin, new Vector3(0f, -1f, 0f), out var hit))
        {
            hitY = hit.Point.Y;
            return true;
        }

        hitY = float.NegativeInfinity;
        return false;
    }

    // "Ground" = a surface at about your standing height (or higher), not a big drop.
    private bool IsGround(float x, float z, float standY)
    {
        return CastDown(x, standY, z, out var y) && y >= standY - config.DropThreshold;
    }

    // Is there a wall within maxDistance straight ahead at about chest height?
    private static unsafe bool WallWithin(Vector3 pos, float standY, float ux, float uz, float maxDistance)
    {
        var origin = new Vector3(pos.X, standY + WallHeight, pos.Z);
        var direction = new Vector3(ux, 0f, uz);
        if (Collision.Raycast(Collision.Preset, false, origin, direction, out var hit))
        {
            return (hit.Point - origin).Length() <= maxDistance;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // Ground checks that account for the size of your body.
    // The game lets you keep standing a little way past the end of the collision. Two models are used:
    //   1. Simple: the ground test is taken "radius" yalms BEHIND the point it is about. This is right whenever
    //      there is ground behind you along your path, which is the normal case.
    //   2. Disk: used only when the simple one finds nothing, e.g. you are floating off the SIDE of something.
    //      The ground test then looks anywhere under your body's circle.
    // ------------------------------------------------------------------
    private const float MinDiskRadius = 0.05f;

    // How much bigger than the standing zone the "am I standing right now?" check is.
    private const float GroundSlack = 0.08f;

    // In disk mode, a point only counts as support if it is at about your standing height.
    private const float SupportBelow = 0.35f;
    private const float SupportAbove = 0.30f;

    // While scanning ahead, only the part of the disk behind you (and beside you) can still be holding you up.
    private const float ScanArc = 1.92f;   // about 110 degrees either side of straight behind

    // (fraction of the radius, number of sample points). Dense enough to catch thin rails and arms.
    private static readonly (float Fraction, int Count)[] Rings = { (1.0f, 24), (0.66f, 16), (0.33f, 8) };

    private bool IsSupport(float x, float z, float standY)
    {
        return CastDown(x, standY, z, out var y) && y >= standY - SupportBelow && y <= standY + SupportAbove;
    }

    // Disk model: is there support anywhere in the rear and side parts of the disk around (x, z)?
    private bool IsGroundNear(float x, float z, float standY, float radius, float ux, float uz)
    {
        if (IsGround(x, z, standY))
            return true;

        if (radius < MinDiskRadius)
            return false;

        var rear = MathF.Atan2(-uz, -ux);
        foreach (var (fraction, count) in Rings)
        {
            var r = radius * fraction;
            var turn = 2f * MathF.PI / count;
            for (var k = 0; k < count; k++)
            {
                var steps = (k + 1) / 2;
                if (steps * turn > ScanArc)
                    break;

                var a = rear + (k % 2 == 1 ? steps : -steps) * turn;
                if (IsSupport(x + MathF.Cos(a) * r, z + MathF.Sin(a) * r, standY))
                    return true;
            }
        }

        return false;
    }

    // Disk model, whole circle: the height of the ground closest to your feet under your body.
    // nearestBelow is only meaningful when this returns false.
    private bool FindGroundHeight(Vector3 pos, float radius, out float groundY, out float nearestBelow)
    {
        nearestBelow = float.NaN;

        var centreHit = CastDown(pos.X, pos.Y, pos.Z, out groundY);
        if (centreHit)
        {
            if (MathF.Abs(groundY - pos.Y) <= RayLift)
                return true;

            nearestBelow = pos.Y - groundY;
        }

        if (radius >= MinDiskRadius)
        {
            var reach = radius + GroundSlack;
            var bestDiff = float.MaxValue;
            var best = 0f;
            foreach (var (fraction, count) in Rings)
            {
                var r = reach * fraction;
                for (var k = 0; k < count; k++)
                {
                    var a = k * 2f * MathF.PI / count;
                    if (CastDown(pos.X + MathF.Cos(a) * r, pos.Z + MathF.Sin(a) * r, pos.Y, out var y))
                    {
                        var diff = MathF.Abs(y - pos.Y);
                        if (diff <= RayLift && diff < bestDiff)
                        {
                            bestDiff = diff;
                            best = y;
                        }

                        if (float.IsNaN(nearestBelow) || diff < MathF.Abs(nearestBelow))
                            nearestBelow = pos.Y - y;
                    }
                }
            }

            if (bestDiff < float.MaxValue)
            {
                groundY = best;
                return true;
            }
        }

        return false;
    }

    // Is there still ground at distance d along your path?
    private bool GroundAt(Vector3 pos, float ux, float uz, float radius, float groundY, bool useDisk, float d)
    {
        if (useDisk)
            return IsGroundNear(pos.X + ux * d, pos.Z + uz * d, groundY, radius, ux, uz);

        // Simple model: test "radius" yalms behind the point.
        return IsGround(pos.X + ux * (d - radius), pos.Z + uz * (d - radius), groundY);
    }

    // (ux, uz) must be the direction you are walking, as a length-1 vector.
    // "radius" is how far past the end of the collision you can still stand. EdgeDistance then becomes the
    // distance to the last place you can still stand, not to the end of the collision. 0 = the collision itself.
    public EdgeResult Evaluate(Vector3 pos, float ux, float uz, float radius)
    {
        var result = new EdgeResult();
        Collision.Preset = config.CollisionPreset;
        Collision.Sweep = false;

        // What are you standing on? First try the simple model (ground behind you along your path).
        var useDisk = false;
        var nearestBelow = float.NaN;
        float groundY;

        var bx = pos.X - ux * radius;
        var bz = pos.Z - uz * radius;

        if (CastDown(bx, pos.Y, bz, out groundY) && MathF.Abs(groundY - pos.Y) <= RayLift)
        {
            // Simple model works.
        }
        else if (FindGroundHeight(pos, radius, out groundY, out nearestBelow))
        {
            // Floating off the side of something: use the disk model.
            useDisk = true;
        }
        else
        {
            result.NearestGroundBelow = nearestBelow;
            return result;
        }

        result.Grounded = true;
        result.GroundY = groundY;

        var range = MathF.Max(config.ScanRange, CoarseStep * 2f);
        var previous = 0f;

        for (var d = CoarseStep; d <= range + 0.0001f; d += CoarseStep)
        {
            if (!GroundAt(pos, ux, uz, radius, groundY, useDisk, d))
            {
                // The ground ends somewhere between "previous" and "d". Narrow it down.
                var lo = previous;
                var hi = d;
                for (var i = 0; i < RefineSteps; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (GroundAt(pos, ux, uz, radius, groundY, useDisk, mid))
                        lo = mid;
                    else
                        hi = mid;
                }

                result.HasEdge = true;
                result.EdgeDistance = lo;
                break;
            }

            previous = d;
        }

        if (result.HasEdge && config.IgnoreWalls)
            result.WallAhead = WallWithin(pos, groundY, ux, uz, result.EdgeDistance + 0.15f);

        return result;
    }
}
