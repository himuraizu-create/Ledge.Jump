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
    public int Version { get; set; } = 1;

    // Master switch
    public bool Enabled { get; set; } = true;

    // How far BEFORE the recorded spot (yalms) the jump is allowed to fire.
    public float ZoneRadius { get; set; } = 0.1f;

    // How far to the left/right of the spot you can be and still count.
    public float SideLeeway { get; set; } = 0.8f;

    // The trigger window is at least this many times the distance you move per frame,
    // so a low frame rate cannot make you skip over the spot.
    public float AutoWiden { get; set; } = 1.5f;

    // Print a line in chat for each jump / near-miss.
    public bool Debug { get; set; } = true;

    public List<Spot> Spots { get; set; } = new();
}
