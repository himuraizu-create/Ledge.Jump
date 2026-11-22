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
    // Body-shaped ground checks.
    // The game lets you stand while ANY part of your body's circle (radius = "radius" below) overlaps ground.
    // So "can I stand here?" is tested over a whole disk, not at a single point. That works no matter which
    // side of the platform you are floating off, and for edges that are not square-on to your walking direction.
    // ------------------------------------------------------------------
    private const float MinDiskRadius = 0.05f;

    // (fraction of the radius, number of sample points). Outer ring first: it is the one that decides the edge.
    private static readonly (float Fraction, int Count)[] Rings = { (1.0f, 16), (0.66f, 10), (0.33f, 6) };

    // Is there ground anywhere under the disk around (x, z)? The centre is tried first, then points around the
    // disk starting on the side you are walking away from, because that is where your ground usually is.
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
                var a = rear + (k % 2 == 1 ? steps : -steps) * turn;
                if (IsGround(x + MathF.Cos(a) * r, z + MathF.Sin(a) * r, standY))
                    return true;
            }
        }

        return false;
    }

    // The height of the ground you are standing on: directly under you if there is some, otherwise the closest
    // ground under your body's circle (you are floating off the side of something).
    private bool FindGroundHeight(Vector3 pos, float radius, out float groundY)
    {
        if (CastDown(pos.X, pos.Y, pos.Z, out groundY) && MathF.Abs(groundY - pos.Y) <= RayLift)
            return true;

        if (radius >= MinDiskRadius)
        {
            var bestDiff = float.MaxValue;
            var best = 0f;
            foreach (var (fraction, count) in Rings)
            {
                var r = radius * fraction;
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

    // (ux, uz) must be the direction you are walking, as a length-1 vector.
    // "radius" is how far past the end of the collision you can still stand. EdgeDistance then becomes the
    // distance to the last place you can still stand, not to the end of the collision. 0 = the collision itself.
    public EdgeResult Evaluate(Vector3 pos, float ux, float uz, float radius)
    {
        var result = new EdgeResult();
        Collision.Preset = config.CollisionPreset;
        Collision.Sweep = config.UseSweep;

        // What are you standing on? If nothing is under you or your body, you are in the air (or in a spot with no collision).
        if (!FindGroundHeight(pos, radius, out var groundY))
            return result;

        result.Grounded = true;
        result.GroundY = groundY;

        var range = MathF.Max(config.ScanRange, CoarseStep * 2f);
        var previous = 0f;

        for (var d = CoarseStep; d <= range + 0.0001f; d += CoarseStep)
        {
            if (!IsGroundNear(pos.X + ux * d, pos.Z + uz * d, groundY, radius, ux, uz))
            {
                // The ground ends somewhere between "previous" and "d". Narrow it down.
                var lo = previous;
                var hi = d;
                for (var i = 0; i < RefineSteps; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (IsGroundNear(pos.X + ux * mid, pos.Z + uz * mid, groundY, radius, ux, uz))
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
