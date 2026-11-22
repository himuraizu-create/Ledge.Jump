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

    // Optional: the far end of the platform (the furthest back you can stand). With it, the plugin
    // measures distance along the platform itself instead of along wherever you happen to be heading.
    public bool HasBack { get; set; }
    public float BackX { get; set; }
    public float BackZ { get; set; }

    // Per-spot timing offset in yalms, added to the global jump point.
    // Positive = jump earlier, negative = jump later. The auto-tuner changes this after each jump.
    public float Adjust { get; set; }

    // Statistics shown in the window.
    public int Attempts { get; set; }
    public string LastOutcome { get; set; } = "";
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

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

    // No longer used (the jump now fires on the frame nearest the jump point). Kept so old saves still load.
    public float AutoWiden { get; set; } = 1.5f;

    // After every jump at a recorded spot, measure where you actually left the ground and nudge that spot's
    // timing offset so the next jump is closer to the edge.
    public bool AutoTune { get; set; } = true;

    // How much of the measured error to correct each time (0.1 = gentle, 1.0 = all of it at once).
    public float TuneStrength { get; set; } = 0.5f;

    // How far (yalms) to jump earlier after you fell off before the jump took effect.
    public float FailStep { get; set; } = 0.08f;

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

    // ---------------- Learn an edge by walking off it ----------------

    // While learning, frames where your height is within this many yalms of your standing height count as "still standing".
    public float LearnTolerance { get; set; } = 0.01f;
}
