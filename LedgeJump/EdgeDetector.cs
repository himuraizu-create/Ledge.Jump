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
    private const int RefineSteps = 5;      // halvings used to pin down the exact edge position
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
        if (Collision.Raycast(Collision.Preset, origin, new Vector3(0f, -1f, 0f), out var hit))
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
        if (Collision.Raycast(Collision.Preset, origin, direction, out var hit))
        {
            return (hit.Point - origin).Length() <= maxDistance;
        }

        return false;
    }

    // (ux, uz) must be the direction you are walking, as a length-1 vector.
    public EdgeResult Evaluate(Vector3 pos, float ux, float uz)
    {
        var result = new EdgeResult();
        Collision.Preset = config.CollisionPreset;

        // What are you standing on? If nothing is directly under you, you are in the air (or in a spot with no collision).
        if (!CastDown(pos.X, pos.Y, pos.Z, out var groundY) || MathF.Abs(groundY - pos.Y) > RayLift)
            return result;

        result.Grounded = true;
        result.GroundY = groundY;

        var range = MathF.Max(config.ScanRange, CoarseStep * 2f);
        var previous = 0f;

        for (var d = CoarseStep; d <= range + 0.0001f; d += CoarseStep)
        {
            if (!IsGround(pos.X + ux * d, pos.Z + uz * d, groundY))
            {
                // The ground ends somewhere between "previous" and "d". Narrow it down.
                var lo = previous;
                var hi = d;
                for (var i = 0; i < RefineSteps; i++)
                {
                    var mid = (lo + hi) * 0.5f;
                    if (IsGround(pos.X + ux * mid, pos.Z + uz * mid, groundY))
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
