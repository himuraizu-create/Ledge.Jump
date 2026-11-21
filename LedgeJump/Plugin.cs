using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace LedgeJump;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/ledgejump";

    // General Action #2 is "Jump" (the same action as /gaction jump)
    private const uint JumpGeneralActionId = 2;

    private const float MinStep = 0.003f;        // smaller movement per frame than this = standing still
    private const float MaxStep = 3.0f;          // bigger movement per frame than this = teleport/lag, ignore
    private const double CooldownSeconds = 0.6;  // wait this long after a jump before another can fire
    private const float WatchExtra = 2.5f;       // how far outside the zone to watch for near-misses

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IChatGui chatGui;
    private readonly IPluginLog log;

    private readonly Configuration config;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private bool haveLast;
    private Vector3 last;
    private double lastTime;
    private double cooldownUntil;

    // Near-miss tracking (per spot)
    private readonly Dictionary<Spot, float> closest = new();
    private readonly HashSet<Spot> jumpedThisVisit = new();

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        IObjectTable objectTable,
        IClientState clientState,
        IChatGui chatGui,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.chatGui = chatGui;
        this.log = log;

        config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Ledge Jump. Type /ledgejump for the list of commands.",
        });

        framework.Update += OnUpdate;

        log.Information("Ledge Jump loaded.");
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        commandManager.RemoveHandler(CommandName);
    }

    // ------------------------------------------------------------------
    // Runs every frame
    // ------------------------------------------------------------------
    private void OnUpdate(IFramework _)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            haveLast = false;
            return;
        }

        var pos = player.Position;
        var now = clock.Elapsed.TotalSeconds;

        if (!haveLast)
        {
            last = pos;
            lastTime = now;
            haveLast = true;
            return;
        }

        var dx = pos.X - last.X;
        var dz = pos.Z - last.Z;
        var step = MathF.Sqrt(dx * dx + dz * dz);
        var dt = now - lastTime;
        last = pos;
        lastTime = now;

        if (!config.Enabled || config.Spots.Count == 0)
            return;

        if (step > MaxStep)
            return;

        var territory = (uint)clientState.TerritoryType;
        var canJump = now >= cooldownUntil && step >= MinStep;

        // Your direction of travel this frame
        var ux = canJump ? dx / step : 0f;
        var uz = canJump ? dz / step : 0f;

        // The trigger window is at least ZoneRadius, and grows with your speed per frame
        var reach = MathF.Max(config.ZoneRadius, step * config.AutoWiden);

        foreach (var spot in config.Spots)
        {
            if (spot.Territory != territory)
                continue;

            var rx = spot.X - pos.X;
            var rz = spot.Z - pos.Z;
            var dist = MathF.Sqrt(rx * rx + rz * rz);

            // Near-miss bookkeeping: remember how close you got on this run
            if (dist <= config.ZoneRadius + WatchExtra + step)
            {
                if (!closest.TryGetValue(spot, out var best) || dist < best)
                    closest[spot] = dist;
            }
            else if (closest.TryGetValue(spot, out var finalBest))
            {
                if (!jumpedThisVisit.Contains(spot) && config.Debug)
                    Print($"No jump. Closest you got to the spot: {F(finalBest)}");
                closest.Remove(spot);
                jumpedThisVisit.Remove(spot);
            }

            if (!canJump)
                continue;

            var along = rx * ux + rz * uz;              // how far ahead the spot is (negative = behind you)
            var side = MathF.Abs(rx * uz - rz * ux);    // how far off your line of travel it is

            if (side <= config.SideLeeway && along >= 0f && along <= reach)
            {
                var accepted = DoJump();
                cooldownUntil = now + CooldownSeconds;
                jumpedThisVisit.Add(spot);
                canJump = false;

                if (config.Debug)
                {
                    Print($"JUMP | {F(along)} before the spot | {F(side)} off to the side | " +
                          $"moved {F(step)} per frame | {dt * 1000:0} ms per frame | accepted: {accepted}");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // The jump itself (same as /gaction jump)
    // ------------------------------------------------------------------
    private unsafe bool DoJump()
    {
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
            return false;

        return actionManager->UseAction(ActionType.GeneralAction, JumpGeneralActionId);
    }

    // ------------------------------------------------------------------
    // Chat commands
    // ------------------------------------------------------------------
    private void OnCommand(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "help";

        switch (sub)
        {
            case "add":
                AddSpot(string.Join(' ', parts.Skip(1)));
                break;

            case "list":
            case "status":
                PrintStatus();
                break;

            case "remove":
                RemoveSpot(parts);
                break;

            case "clear":
                config.Spots.Clear();
                closest.Clear();
                jumpedThisVisit.Clear();
                Save();
                Print("All spots removed.");
                break;

            case "on":
                config.Enabled = true;
                Save();
                Print("Ledge Jump is ON.");
                break;

            case "off":
                config.Enabled = false;
                Save();
                Print("Ledge Jump is OFF.");
                break;

            case "toggle":
                config.Enabled = !config.Enabled;
                Save();
                Print(config.Enabled ? "Ledge Jump is ON." : "Ledge Jump is OFF.");
                break;

            case "radius":
                SetNumber(parts, "radius", 0f, 3f, v => config.ZoneRadius = v, () => config.ZoneRadius);
                break;

            case "side":
                SetNumber(parts, "side", 0.05f, 5f, v => config.SideLeeway = v, () => config.SideLeeway);
                break;

            case "widen":
                SetNumber(parts, "widen", 1f, 5f, v => config.AutoWiden = v, () => config.AutoWiden);
                break;

            case "debug":
                config.Debug = parts.Length > 1 ? parts[1].ToLowerInvariant() == "on" : !config.Debug;
                Save();
                Print(config.Debug ? "Debug messages ON." : "Debug messages OFF.");
                break;

            case "coords":
                PrintCoords();
                break;

            case "test":
                Print($"Test jump sent. Game accepted it: {DoJump()}");
                break;

            default:
                PrintHelp();
                break;
        }
    }

    private void AddSpot(string name)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            Print("No character found.");
            return;
        }

        var pos = player.Position;
        var spot = new Spot
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Spot {config.Spots.Count + 1}" : name,
            Territory = (uint)clientState.TerritoryType,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
        };

        config.Spots.Add(spot);
        Save();
        Print($"Added spot #{config.Spots.Count} \"{spot.Name}\" at x = {F(spot.X)}, z = {F(spot.Z)}. " +
              "Now run at it and it will jump.");
    }

    private void RemoveSpot(string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out var number) || number < 1 || number > config.Spots.Count)
        {
            Print("Usage: /ledgejump remove <number>   (see the numbers with /ledgejump list)");
            return;
        }

        var spot = config.Spots[number - 1];
        config.Spots.RemoveAt(number - 1);
        closest.Remove(spot);
        jumpedThisVisit.Remove(spot);
        Save();
        Print($"Removed spot #{number} \"{spot.Name}\".");
    }

    private void SetNumber(string[] parts, string label, float min, float max, Action<float> set, Func<float> get)
    {
        if (parts.Length < 2 ||
            !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            Print($"{label} is currently {F(get())}. To change it: /ledgejump {label} <number>");
            return;
        }

        value = Math.Clamp(value, min, max);
        set(value);
        Save();
        Print($"{label} set to {F(value)}.");
    }

    private void PrintCoords()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            Print("No character found.");
            return;
        }

        var p = player.Position;
        Print($"x = {F(p.X)}, y = {F(p.Y)}, z = {F(p.Z)}  (zone {(uint)clientState.TerritoryType})");
    }

    private void PrintStatus()
    {
        Print($"{(config.Enabled ? "ON" : "OFF")} | radius {F(config.ZoneRadius)} | side {F(config.SideLeeway)} | " +
              $"widen {F(config.AutoWiden)} | debug {(config.Debug ? "on" : "off")} | " +
              $"current zone {(uint)clientState.TerritoryType}");

        if (config.Spots.Count == 0)
        {
            Print("No spots yet. Stand at the very tip of a platform and type /ledgejump add");
            return;
        }

        for (var i = 0; i < config.Spots.Count; i++)
        {
            var s = config.Spots[i];
            Print($"#{i + 1} \"{s.Name}\"  x = {F(s.X)}, z = {F(s.Z)}  (zone {s.Territory})");
        }
    }

    private void PrintHelp()
    {
        Print("Commands:");
        Print("/ledgejump add [name]  - record your current position as a jump spot (stand at the very tip first)");
        Print("/ledgejump list        - show settings and your spots");
        Print("/ledgejump remove <n>  - remove spot number n");
        Print("/ledgejump clear       - remove all spots");
        Print("/ledgejump on | off | toggle");
        Print("/ledgejump radius <n>  - how far before the spot the jump fires (default 0.1)");
        Print("/ledgejump side <n>    - sideways leeway (default 0.8)");
        Print("/ledgejump widen <n>   - frame-rate safety multiplier (default 1.5)");
        Print("/ledgejump debug [on|off] - chat messages for each jump");
        Print("/ledgejump coords      - print your coordinates");
        Print("/ledgejump test        - make your character jump once");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private void Save() => pluginInterface.SavePluginConfig(config);

    private void Print(string message) => chatGui.Print($"[LedgeJump] {message}");

    private static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
