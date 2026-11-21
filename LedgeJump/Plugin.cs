using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
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
    private readonly EdgeDetector edgeDetector;
    private readonly WindowSystem windowSystem = new("LedgeJump");
    private readonly ConfigWindow configWindow;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private bool haveLast;
    private Vector3 last;
    private double lastTime;
    private double cooldownUntil;

    // Near-miss tracking (per spot)
    private readonly Dictionary<Spot, float> closest = new();
    private readonly HashSet<Spot> jumpedThisVisit = new();

    // Latest automatic-edge reading, shown live in the options window
    internal EdgeResult LastEdge { get; private set; }
    internal bool HaveEdgeReading { get; private set; }

    internal uint CurrentTerritory => (uint)clientState.TerritoryType;

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
        edgeDetector = new EdgeDetector(config);

        configWindow = new ConfigWindow(this, config, Save);
        windowSystem.AddWindow(configWindow);

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
        pluginInterface.UiBuilder.OpenMainUi += ToggleWindow;

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Ledge Jump. Opens the options window. Use /ledgejump help for chat commands.",
        });

        framework.Update += OnUpdate;

        log.Information("Ledge Jump loaded.");
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        commandManager.RemoveHandler(CommandName);

        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        pluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        windowSystem.RemoveAllWindows();
    }

    private void ToggleWindow() => configWindow.Toggle();

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

        var wantAuto = config.Enabled && config.UseAutoEdge;
        var wantSpots = config.Enabled && config.UseSpots && config.Spots.Count > 0;
        var wantReadout = configWindow.IsOpen;

        if (!wantAuto && !wantSpots && !wantReadout)
            return;

        if (step > MaxStep)
            return;

        var canJump = config.Enabled && now >= cooldownUntil && step >= MinStep;

        // Your direction of travel this frame (length 1). Only meaningful while you are moving.
        var ux = step >= MinStep ? dx / step : 0f;
        var uz = step >= MinStep ? dz / step : 0f;

        // -------- Automatic edge detection (the game's collision) --------
        if (step >= MinStep && (wantAuto || wantReadout))
        {
            var edge = edgeDetector.Evaluate(pos, ux, uz);
            LastEdge = edge;
            HaveEdgeReading = true;

            // Jump when the point (edge - lead) will be passed before the next frame.
            if (wantAuto && canJump && edge.Grounded && edge.HasEdge && !edge.WallAhead &&
                edge.EdgeDistance - config.EdgeLead <= step * config.EdgeWiden)
            {
                var accepted = DoJump();
                cooldownUntil = now + CooldownSeconds;
                canJump = false;

                if (config.Debug)
                {
                    Print($"AUTO JUMP | edge was {F(edge.EdgeDistance)} ahead | " +
                          $"moved {F(step)} per frame | {dt * 1000:0} ms per frame | accepted: {accepted}");
                }
            }
        }

        // -------- Recorded spots --------
        if (!wantSpots)
            return;

        var territory = CurrentTerritory;

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
                    Print($"No jump. Closest you got to \"{spot.Name}\": {F(finalBest)}");
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
                    Print($"JUMP \"{spot.Name}\" | {F(along)} before the spot | {F(side)} off to the side | " +
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
    // Things the options window calls
    // ------------------------------------------------------------------
    internal void TestJump() => Print($"Test jump sent. Game accepted it: {DoJump()}");

    internal void AddSpot(string name)
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
            Name = string.IsNullOrWhiteSpace(name) ? $"Spot {config.Spots.Count + 1}" : name.Trim(),
            Territory = CurrentTerritory,
            X = pos.X,
            Y = pos.Y,
            Z = pos.Z,
        };

        config.Spots.Add(spot);
        Save();
        Print($"Added spot #{config.Spots.Count} \"{spot.Name}\" at x = {F(spot.X)}, z = {F(spot.Z)}. " +
              "Now run at it and it will jump.");
    }

    internal void MoveSpotHere(int index)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || index < 0 || index >= config.Spots.Count)
            return;

        var pos = player.Position;
        var spot = config.Spots[index];
        spot.Territory = CurrentTerritory;
        spot.X = pos.X;
        spot.Y = pos.Y;
        spot.Z = pos.Z;
        closest.Remove(spot);
        jumpedThisVisit.Remove(spot);
        Save();
        Print($"Spot \"{spot.Name}\" moved to x = {F(spot.X)}, z = {F(spot.Z)}.");
    }

    internal void RemoveSpotAt(int index)
    {
        if (index < 0 || index >= config.Spots.Count)
            return;

        var spot = config.Spots[index];
        config.Spots.RemoveAt(index);
        closest.Remove(spot);
        jumpedThisVisit.Remove(spot);
        Save();
        Print($"Removed spot \"{spot.Name}\".");
    }

    // ------------------------------------------------------------------
    // Chat commands
    // ------------------------------------------------------------------
    private void OnCommand(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "ui";

        switch (sub)
        {
            case "ui":
            case "config":
            case "settings":
            case "window":
                configWindow.Toggle();
                break;

            case "add":
                AddSpot(string.Join(' ', parts.Skip(1)));
                break;

            case "list":
            case "status":
                PrintStatus();
                break;

            case "remove":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var number) ||
                    number < 1 || number > config.Spots.Count)
                    Print("Usage: /ledgejump remove <number>   (see the numbers with /ledgejump list)");
                else
                    RemoveSpotAt(number - 1);
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

            case "auto":
                config.UseAutoEdge = parts.Length > 1 ? parts[1].ToLowerInvariant() == "on" : !config.UseAutoEdge;
                Save();
                Print(config.UseAutoEdge ? "Automatic edge detection ON." : "Automatic edge detection OFF.");
                break;

            case "debug":
                config.Debug = parts.Length > 1 ? parts[1].ToLowerInvariant() == "on" : !config.Debug;
                Save();
                Print(config.Debug ? "Chat messages ON." : "Chat messages OFF.");
                break;

            case "coords":
                PrintCoords();
                break;

            case "test":
                TestJump();
                break;

            default:
                PrintHelp();
                break;
        }
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
        Print($"x = {F(p.X)}, y = {F(p.Y)}, z = {F(p.Z)}  (zone {CurrentTerritory})");
    }

    private void PrintStatus()
    {
        Print($"{(config.Enabled ? "ON" : "OFF")} | spots {(config.UseSpots ? "on" : "off")} | " +
              $"auto edge {(config.UseAutoEdge ? "on" : "off")} | current zone {CurrentTerritory}");

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
        Print("/ledgejump                - open the options window");
        Print("/ledgejump add [name]     - record your position as a jump spot");
        Print("/ledgejump list           - show your spots");
        Print("/ledgejump remove <n>     - remove spot number n");
        Print("/ledgejump on | off | toggle");
        Print("/ledgejump auto [on|off]  - automatic edge detection");
        Print("/ledgejump debug [on|off] - chat messages for each jump");
        Print("/ledgejump coords         - print your coordinates");
        Print("/ledgejump test           - make your character jump once");
        Print("Sliders for all the tuning numbers are in the options window.");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private void Save() => pluginInterface.SavePluginConfig(config);

    private void Print(string message) => chatGui.Print($"[LedgeJump] {message}");

    private static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
