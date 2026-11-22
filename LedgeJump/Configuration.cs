using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace LedgeJump;

[Serializable]
public class Spot
{
    public string Name { get; set; } = "";
    public uint Territory { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    // ---------------- General ----------------

    // Master switch
    public bool Enabled { get; set; } = true;

    // Print a line in chat for each jump / near-miss.
    public bool Debug { get; set; } = true;

    // ---------------- Recorded spots ----------------

    // Use the spots you recorded by hand.
    public bool UseSpots { get; set; } = true;

    // How far BEFORE the recorded spot (yalms) the jump is allowed to fire.
    public float ZoneRadius { get; set; } = 0.1f;

    // How far to the left/right of the spot you can be and still count.
    public float SideLeeway { get; set; } = 0.8f;

    // The trigger window is at least this many times the distance you move per frame,
    // so a low frame rate cannot make you skip over the spot.
    public float AutoWiden { get; set; } = 1.5f;

    public List<Spot> Spots { get; set; } = new();

    // ---------------- Automatic edge detection (uses the game's collision) ----------------

    // Jump automatically at the edge of whatever you are standing on.
    public bool UseAutoEdge { get; set; } = false;

    // How far BEFORE the real edge (yalms) the jump is pressed.
    public float EdgeLead { get; set; } = 0.05f;

    // Frame safety: the trigger window is at least this many times the distance you move per frame.
    public float EdgeWiden { get; set; } = 1.0f;

    // Ground that is lower than this many yalms below you counts as "a fall".
    public float DropThreshold { get; set; } = 1.0f;

    // How far ahead (yalms) to look for an edge.
    public float ScanRange { get; set; } = 1.5f;

    // Do not jump when a wall is what is ahead of you (instead of a drop).
    public bool IgnoreWalls { get; set; } = true;

    // Which collision filter the automatic edge detection uses (see Collision.Filters). 0 = Standard.
    public int CollisionPreset { get; set; } = 4;

    // Use a body-sized sweep instead of a thin ray for the ground checks.
    public bool UseSweep { get; set; } = false;

    // How far past the end of the collision (yalms) you can still stand. Measure it with /ledgejump margin.
    public float StandingMargin { get; set; } = 0f;

    // Instead of the manual number above, use the size of your own hitbox (the ring you see when you target yourself).
    // The game lets you keep standing while that ring still overlaps the ground.
    public bool AutoMargin { get; set; } = true;

    // Fine-tune for the automatic margin: 1.0 = exactly your hitbox radius.
    public float MarginScale { get; set; } = 1.27f;

    // ---------------- Learn an edge by walking off it ----------------

    // While learning, frames where your height is within this many yalms of your standing height count as "still standing".
    public float LearnTolerance { get; set; } = 0.01f;
}
