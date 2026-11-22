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

    // "Learn an edge" mode: you walk off a ledge by hand, and the plugin records the last place you stood.
    // It needs no collision data, because it only watches your own height. Your character stays level
    // while standing on something, so the game's idea of "standable" shows up in your movement.
    private const int LearnBufferSize = 90;      // frames of history kept while learning (about 1.5 s at 60 fps)
    private const float LearnFallDepth = 0.6f;   // you must drop this far below your standing height to count as a fall
    private readonly List<Vector3> learnBuffer = new();

    internal bool IsLearning { get; private set; }
    internal string LearnStatus { get; private set; } = "";

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

        // After a jump at a spot, watch where you actually leave the ground.
        if (pending != null)
            UpdatePending(pos, now, step);

        // While learning, the plugin only watches you. It does not jump for you.
        if (IsLearning)
        {
            UpdateLearning(pos, step);
            return;
        }

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

        foreach (var spot in config.Spots)
        {
            if (spot.Territory != territory)
                continue;

            var rx = spot.X - pos.X;
            var rz = spot.Z - pos.Z;
            var dist = MathF.Sqrt(rx * rx + rz * rz);

            // Where this spot wants the jump pressed: the global jump point plus its own tuned offset
            var lead = SpotLead(spot);

            // Near-miss bookkeeping: remember how close you got on this run
            if (dist <= MathF.Max(lead, 0f) + WatchExtra + step)
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

            // Which way is "toward the edge"? The platform's own line if you set a back end for this spot,
            // otherwise the direction you are moving this frame.
            var dirX = ux;
            var dirZ = uz;
            var stepAlong = step;
            if (spot.HasBack)
            {
                var ax = spot.X - spot.BackX;
                var az = spot.Z - spot.BackZ;
                var len = MathF.Sqrt(ax * ax + az * az);
                if (len > 0.1f)
                {
                    dirX = ax / len;
                    dirZ = az / len;
                    stepAlong = dx * dirX + dz * dirZ;   // how far you moved toward the edge this frame
                }
            }

            if (stepAlong < MinStep)
                continue;   // not moving toward the edge

            var along = rx * dirX + rz * dirZ;              // how far ahead the spot is (negative = past it)
            var side = MathF.Abs(rx * dirZ - rz * dirX);    // how far off the line to the edge you are
            var remaining = along - lead;                   // distance left until the jump point

            // Press on the frame nearest the jump point: the first frame where less than half a step remains.
            // (The old rule fired on the first frame within a window, which was always a little early.)
            if (side <= config.SideLeeway && remaining <= stepAlong * 0.5f && remaining > -stepAlong)
            {
                var accepted = DoJump();
                cooldownUntil = now + CooldownSeconds;
                jumpedThisVisit.Add(spot);
                canJump = false;

                if (accepted)
                {
                    pending = new PendingJump
                    {
                        Spot = spot,
                        PressTime = now,
                        PressAlong = along,
                        Lead = lead,
                        DirX = dirX,
                        DirZ = dirZ,
                        StandY = pos.Y,
                        Speed = dt > 0.0001 ? stepAlong / (float)dt : 0f,
                        LastLevelPos = pos,
                    };
                }

                if (config.Debug)
                {
                    Print($"JUMP \"{spot.Name}\" | pressed {F(along)} before the spot (jump point {F(lead)}) | " +
                          $"{F(side)} off to the side | moved {F(stepAlong)} per frame | {dt * 1000:0} ms per frame | " +
                          $"accepted: {accepted}");
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // Jump timing: measure where you really left the ground, then nudge the spot
    // ------------------------------------------------------------------
    private const float TuneDeadband = 0.04f;   // errors smaller than this (yalms) are treated as noise

    private sealed class PendingJump
    {
        public Spot Spot = null!;
        public double PressTime;
        public float PressAlong;
        public float Lead;
        public float DirX;
        public float DirZ;
        public float StandY;
        public float Speed;
        public int Frames;
        public Vector3 LastLevelPos;
    }

    private PendingJump? pending;
    private readonly List<string> calibrationLog = new();

    internal IReadOnlyList<string> CalibrationLog => calibrationLog;

    private float SpotLead(Spot spot) => Math.Clamp(config.ZoneRadius + spot.Adjust, -0.5f, 2.0f);

    private static float AlongOf(PendingJump p, Vector3 v) =>
        (p.Spot.X - v.X) * p.DirX + (p.Spot.Z - v.Z) * p.DirZ;

    // Called every frame after a jump was pressed at a spot, until the outcome is known.
    private void UpdatePending(Vector3 pos, double now, float step)
    {
        var p = pending;
        if (p == null)
            return;

        // Teleport or loading screen: forget it.
        if (step > MaxStep)
        {
            pending = null;
            return;
        }

        p.Frames++;

        // Left the ground: your height rose above where you were standing.
        if (pos.Y > p.StandY + 0.03f)
        {
            // The takeoff happened somewhere between the last level frame and this one.
            var along = (AlongOf(p, p.LastLevelPos) + AlongOf(p, pos)) * 0.5f;
            FinishPending(p, true, along, (now - p.PressTime) * 1000.0);
            return;
        }

        // Fell: dropped well below standing height without ever rising.
        if (pos.Y < p.StandY - 0.3f)
        {
            FinishPending(p, false, AlongOf(p, p.LastLevelPos), (now - p.PressTime) * 1000.0);
            return;
        }

        // Nothing conclusive after a second: give up on this one.
        if (now - p.PressTime > 1.0)
        {
            pending = null;
            return;
        }

        if (MathF.Abs(pos.Y - p.StandY) <= 0.03f)
            p.LastLevelPos = pos;
    }

    private void FinishPending(PendingJump p, bool tookOff, float along, double ms)
    {
        pending = null;

        var spot = p.Spot;
        var before = spot.Adjust;
        spot.Attempts++;

        string outcome;
        if (tookOff)
        {
            // 'along' = how far before the spot you actually left the ground. Positive = too early.
            outcome = along >= 0f
                ? $"left the ground {F(along)} before the edge"
                : $"left the ground {F(-along)} past the edge";

            if (config.AutoTune && MathF.Abs(along) > TuneDeadband)
                spot.Adjust = Math.Clamp(spot.Adjust - along * config.TuneStrength, -0.5f, 1.5f);
        }
        else
        {
            outcome = $"fell off before the jump took effect (last stood {F(along)} from the spot)";

            if (config.AutoTune)
                spot.Adjust = Math.Clamp(spot.Adjust + config.FailStep, -0.5f, 1.5f);
        }

        var change = config.AutoTune ? $"offset {F(before)} -> {F(spot.Adjust)}" : "auto-tune off";
        spot.LastOutcome = $"{outcome} ({change})";

        var line = $"{DateTime.Now:HH:mm:ss} \"{spot.Name}\" #{spot.Attempts}: {outcome} | " +
                   $"pressed {F(p.PressAlong)} before the spot (jump point {F(p.Lead)}) | " +
                   $"{p.Speed:0.0} yalms/s | {ms:0} ms ({p.Frames} frames) until result | {change}";

        calibrationLog.Add(line);
        if (calibrationLog.Count > 40)
            calibrationLog.RemoveAt(0);

        Save();

        if (config.Debug)
            Print($"RESULT \"{spot.Name}\": {outcome} | {change}");
    }

    internal void SetBackHere(int index)
    {
        var player = objectTable.LocalPlayer;
        if (player == null || index < 0 || index >= config.Spots.Count)
            return;

        var pos = player.Position;
        var spot = config.Spots[index];
        var length = MathF.Sqrt((spot.X - pos.X) * (spot.X - pos.X) + (spot.Z - pos.Z) * (spot.Z - pos.Z));

        if (length < 0.2f)
        {
            Print("That is too close to the edge spot. Stand at the furthest-back point of the platform, then try again.");
            return;
        }

        spot.HasBack = true;
        spot.BackX = pos.X;
        spot.BackZ = pos.Z;
        Save();
        Print($"Back end set for \"{spot.Name}\". The platform is {F(length)} yalms long.");
    }

    internal void ClearBack(int index)
    {
        if (index < 0 || index >= config.Spots.Count)
            return;

        config.Spots[index].HasBack = false;
        Save();
    }

    internal void ResetTuning(int index)
    {
        if (index < 0 || index >= config.Spots.Count)
            return;

        var spot = config.Spots[index];
        spot.Adjust = 0f;
        spot.Attempts = 0;
        spot.LastOutcome = "";
        Save();
        Print($"Timing offset reset for \"{spot.Name}\".");
    }

    internal void ClearCalibrationLog() => calibrationLog.Clear();

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
    // Learn an edge by walking off it
    // ------------------------------------------------------------------
    internal void StartLearning()
    {
        learnBuffer.Clear();
        IsLearning = true;
        LearnStatus = "Learning... walk off the edge you want to record. Do not jump.";
        Print("Learning: walk (do not jump) off the edge you want to record.");
    }

    internal void CancelLearning()
    {
        if (!IsLearning)
            return;

        IsLearning = false;
        learnBuffer.Clear();
        LearnStatus = "Learning cancelled.";
        Print("Learning cancelled.");
    }

    private void UpdateLearning(Vector3 pos, float step)
    {
        // A huge jump in position means a teleport or a loading screen. Start over.
        if (step > MaxStep)
        {
            learnBuffer.Clear();
            return;
        }

        learnBuffer.Add(pos);
        if (learnBuffer.Count > LearnBufferSize)
            learnBuffer.RemoveAt(0);

        var n = learnBuffer.Count;
        if (n < 12)
            return;

        // The highest point in the recent history is the height you were standing at.
        var maxY = float.MinValue;
        for (var i = 0; i < n; i++)
            maxY = MathF.Max(maxY, learnBuffer[i].Y);

        // Have you dropped far enough below it to be falling?
        if (pos.Y > maxY - LearnFallDepth)
            return;

        // The last frame at which you were still at standing height.
        var s = -1;
        for (var i = n - 1; i >= 0; i--)
        {
            if (learnBuffer[i].Y >= maxY - config.LearnTolerance)
            {
                s = i;
                break;
            }
        }

        // You need a steady stretch of walking before that frame. Otherwise this is a fall that
        // started earlier (for example after a jump), not a walk off a ledge.
        if (s < 5)
            return;

        for (var i = s - 5; i <= s; i++)
        {
            if (MathF.Abs(learnBuffer[i].Y - maxY) > 0.05f)
                return;
        }

        var walked = Vector2.Distance(
            new Vector2(learnBuffer[s - 5].X, learnBuffer[s - 5].Z),
            new Vector2(learnBuffer[s].X, learnBuffer[s].Z));
        if (walked < 0.05f)
            return;

        // Did you rise to get here? Then it was a jump, not a walk-off.
        var before = Math.Max(0, s - 10);
        if (maxY - learnBuffer[before].Y > 0.1f)
        {
            learnBuffer.Clear();
            LearnStatus = "That looked like a jump or a slope, not a walk-off. Press Learn and try again.";
            IsLearning = false;
            Print(LearnStatus);
            return;
        }

        // The true edge is somewhere between the last frame you stood and the next one.
        var a = learnBuffer[s];
        var b = learnBuffer[Math.Min(s + 1, n - 1)];
        var edgeX = (a.X + b.X) * 0.5f;
        var edgeZ = (a.Z + b.Z) * 0.5f;

        var spot = new Spot
        {
            Name = $"Learned {config.Spots.Count + 1}",
            Territory = CurrentTerritory,
            X = edgeX,
            Y = a.Y,
            Z = edgeZ,
        };

        config.Spots.Add(spot);
        Save();

        IsLearning = false;
        learnBuffer.Clear();
        LearnStatus = $"Learned \"{spot.Name}\" at x = {F(spot.X)}, z = {F(spot.Z)}.";
        Print(LearnStatus + " Run at it to test. If it jumps too late, raise the jump point slider.");
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
        spot.Adjust = 0f;
        closest.Remove(spot);
        jumpedThisVisit.Remove(spot);
        pending = null;
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
        pending = null;
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

            case "learn":
                if (IsLearning)
                    CancelLearning();
                else
                    StartLearning();
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
        Print("/ledgejump learn          - walk off an edge by hand and the plugin records it");
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
