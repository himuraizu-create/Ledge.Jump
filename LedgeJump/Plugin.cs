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

    // Size of your character's hitbox (the ring around you when you target yourself), read from the game every frame.
    internal float HitboxRadius { get; private set; }

    // How far past the end of the collision you can still stand, as currently configured.
    internal float EffectiveMargin => config.AutoMargin ? HitboxRadius * config.MarginScale : config.StandingMargin;
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

    private static unsafe float ReadHitboxRadius(nint address)
    {
        if (address == 0)
            return 0f;

        return ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)address)->HitboxRadius;
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
        HitboxRadius = ReadHitboxRadius(player.Address);
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

        // While learning, the plugin only watches you. It does not jump for you.
        if (IsLearning)
        {
            UpdateLearning(pos, step);
            return;
        }

        // Experiment mode (/ledgejump reacttest). Everything else is paused while it runs.
        if (reactTest)
        {
            UpdateReactTest(pos, now);
            return;
        }

        // Measuring mode (/ledgejump margin). Everything else is paused while it runs.
        if (marginTest)
        {
            UpdateMarginTest(pos, dx, dz, step, now);
            return;
        }

        if (config.Debug)
            UpdateJumpReport(pos, now);

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
            var edge = edgeDetector.Evaluate(pos, ux, uz, EffectiveMargin);
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
                    StartJumpReport(pos, ux, uz, edge.EdgeDistance, now);

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
    // Report after each automatic jump: did you take off in time, and by how much?
    // This only prints a line in chat (when chat messages are on). It never changes any setting.
    // ------------------------------------------------------------------
    private bool reportPending;
    private float reportLx;
    private float reportLz;
    private float reportUx;
    private float reportUz;
    private float reportLevelY;
    private float reportPrevY;
    private float reportPrevX;
    private float reportPrevZ;
    private double reportStart;

    private void StartJumpReport(Vector3 pos, float ux, float uz, float distanceToLastStandingPoint, double now)
    {
        reportPending = true;
        reportLx = pos.X + ux * distanceToLastStandingPoint;
        reportLz = pos.Z + uz * distanceToLastStandingPoint;
        reportUx = ux;
        reportUz = uz;
        reportLevelY = pos.Y;
        reportPrevY = pos.Y;
        reportPrevX = pos.X;
        reportPrevZ = pos.Z;
        reportStart = now;
    }

    private void UpdateJumpReport(Vector3 pos, double now)
    {
        if (!reportPending)
            return;

        var y = pos.Y;

        if (y > reportPrevY + 0.02f)
        {
            // Rising: the jump took effect. How far before the last standing point were you?
            var before = (reportLx - reportPrevX) * reportUx + (reportLz - reportPrevZ) * reportUz;
            var after = (reportLx - pos.X) * reportUx + (reportLz - pos.Z) * reportUz;
            var mid = (before + after) * 0.5f;

            string verdict;
            if (mid > 0.2f)
                verdict = "That is early. Lower the Jump point.";
            else if (mid < 0.03f)
                verdict = "Right at the limit. Raise the Jump point a little if some jumps fail.";
            else
                verdict = "Good.";

            Print($"JUMP REPORT: you took off about {F(mid)} yalms before the last place you could stand " +
                  $"(between {F(after)} and {F(before)}). {verdict}");
            reportPending = false;
        }
        else if (y < reportLevelY - 0.02f && y < reportPrevY)
        {
            // Falling before any rise: the jump came too late.
            var past = (pos.X - reportLx) * reportUx + (pos.Z - reportLz) * reportUz;
            Print($"JUMP REPORT: you started falling before the jump took effect. You were about {F(past)} yalms " +
                  "past the last place you could stand. Raise the Jump point.");
            reportPending = false;
        }
        else if (now - reportStart > 1.0)
        {
            Print("JUMP REPORT: no takeoff was seen after the jump was pressed.");
            reportPending = false;
        }

        reportPrevY = y;
        reportPrevX = pos.X;
        reportPrevZ = pos.Z;
    }

    // ------------------------------------------------------------------
    // Measuring: how far past the end of the collision can you keep standing?
    // You walk off ledges by hand. For each one, the plugin remembers where the game's collision ended and where
    // you were when your height first started to drop, and reports the difference. The numbers are only as exact
    // as one frame of movement (about 0.1 yalm), so do several and look at the average.
    // ------------------------------------------------------------------
    private bool marginTest;
    private int marginStable;
    private bool marginHadLevel;
    private float marginLevelY;
    private float marginPrevY;
    private float marginPrevX;
    private float marginPrevZ;
    private bool marginHaveEdge;
    private float marginEdgeX;
    private float marginEdgeZ;
    private float marginUx;
    private float marginUz;
    private double marginEdgeTime;
    private int marginCount;
    private float marginSum;
    private float marginMin;
    private float marginMax;

    private void UpdateMarginTest(Vector3 pos, float dx, float dz, float step, double now)
    {
        var y = pos.Y;

        // Standing steadily?
        if (MathF.Abs(y - marginPrevY) <= 0.004f)
        {
            marginStable++;
            if (marginStable >= 4)
            {
                marginLevelY = y;
                marginHadLevel = true;
            }
        }
        else
        {
            marginStable = 0;
        }

        // You jumped or climbed: that is not a walk-off.
        if (y > marginLevelY + 0.02f)
            marginHadLevel = false;

        // While you walk on ground the collision can see, keep track of where that ground ends ahead of you.
        if (marginHadLevel && step >= MinStep && step <= MaxStep)
        {
            var ux = dx / step;
            var uz = dz / step;
            var e = edgeDetector.Evaluate(pos, ux, uz, 0f);
            if (e.Grounded && e.HasEdge)
            {
                marginHaveEdge = true;
                marginEdgeX = pos.X + ux * e.EdgeDistance;
                marginEdgeZ = pos.Z + uz * e.EdgeDistance;
                marginUx = ux;
                marginUz = uz;
                marginEdgeTime = now;
            }
        }

        // Walked off: you were standing steadily and your height is now dropping.
        if (marginHadLevel && y < marginLevelY - 0.01f && y < marginPrevY)
        {
            marginHadLevel = false;
            marginStable = 0;

            if (!marginHaveEdge || now - marginEdgeTime > 0.6)
            {
                Print("MARGIN: no collision edge was seen just before you fell, so nothing was measured. " +
                      "That usually means the detector cannot see this platform. Check the filter on the Auto Edge tab.");
            }
            else
            {
                // You were last standing somewhere between the previous frame and this one.
                var before = (marginPrevX - marginEdgeX) * marginUx + (marginPrevZ - marginEdgeZ) * marginUz;
                var after = (pos.X - marginEdgeX) * marginUx + (pos.Z - marginEdgeZ) * marginUz;
                var mid = (before + after) * 0.5f;

                marginCount++;
                marginSum += mid;
                marginMin = marginCount == 1 ? mid : MathF.Min(marginMin, mid);
                marginMax = marginCount == 1 ? mid : MathF.Max(marginMax, mid);

                var avg = marginSum / marginCount;
                Print($"MARGIN: you stayed standing about {F(mid)} yalms past the end of the collision " +
                      $"(between {F(before)} and {F(after)}). {marginCount} measured so far: " +
                      $"average {F(avg)}, smallest {F(marginMin)}, largest {F(marginMax)}. " +
                      $"Your hitbox radius is {F(HitboxRadius)}" +
                      (HitboxRadius > 0.001f ? $", so the average is {F(avg / HitboxRadius)} times it." : "."));
            }

            marginHaveEdge = false;
        }

        marginPrevY = y;
        marginPrevX = pos.X;
        marginPrevZ = pos.Z;
    }

    // ------------------------------------------------------------------
    // Experiment: does the game accept a jump just AFTER you walk off an edge?
    // If it does, a jump could be triggered by the fall itself, with no coordinates at all.
    // This only measures. It is off unless you type /ledgejump reacttest.
    // ------------------------------------------------------------------
    private bool reactTest;
    private int reactDelay;       // frames to wait after the drop is noticed before pressing jump
    private int reactState;       // 0 = waiting for you to walk off an edge, 1 = waiting out the delay, 2 = watching the result
    private int reactStable;      // frames in a row at a steady height
    private bool reactHadLevel;   // were you standing steadily just before the drop?
    private float reactLevelY;
    private float reactPrevY;
    private float reactMinY;
    private float reactPressY;
    private int reactFrames;
    private bool reactAccepted;
    private double reactPressTime;
    private double reactFallTime;
    private double reactPrevTime;
    private double reactFrameMs;
    private int reactTried;
    private int reactJumped;

    private void ReactPress(float y, double now)
    {
        reactAccepted = DoJump();
        reactPressY = y;
        reactPressTime = now;
        reactFrames = 0;
        reactState = 2;
    }

    private void UpdateReactTest(Vector3 pos, double now)
    {
        var y = pos.Y;
        if (reactPrevTime > 0)
            reactFrameMs = (now - reactPrevTime) * 1000;

        switch (reactState)
        {
            case 0:
                // Standing steadily?
                if (MathF.Abs(y - reactPrevY) <= 0.004f)
                {
                    reactStable++;
                    if (reactStable >= 4)
                    {
                        reactLevelY = y;
                        reactHadLevel = true;
                    }
                }
                else
                {
                    reactStable = 0;
                }

                // You jumped or climbed: that is not a walk-off.
                if (y > reactLevelY + 0.02f)
                    reactHadLevel = false;

                // Walked off an edge: you were standing steadily and your height is now dropping.
                if (reactHadLevel && y < reactLevelY - 0.01f && y < reactPrevY)
                {
                    reactHadLevel = false;
                    reactState = 1;
                    reactFrames = 0;
                    reactMinY = y;
                    reactFallTime = now;

                    // 0 = press on this very frame, the earliest the plugin can possibly react.
                    if (reactDelay == 0)
                        ReactPress(y, now);
                }

                break;

            case 1:
                reactFrames++;
                reactMinY = MathF.Min(reactMinY, y);

                if (reactFrames >= reactDelay)
                    ReactPress(y, now);

                break;

            case 2:
                reactFrames++;
                reactMinY = MathF.Min(reactMinY, y);

                if (y > reactPrevY + 0.02f)
                {
                    reactTried++;
                    reactJumped++;
                    Print($"REACT TEST: it JUMPED. {ReactWhen()} (already {F(reactLevelY - reactPressY)} below the edge, " +
                          $"frame time {reactFrameMs:0} ms). Tally at this setting: {reactJumped} of {reactTried} jumped.");
                    reactState = 0;
                    reactStable = 0;
                }
                else if (y < reactPressY - 0.6f || now - reactPressTime > 0.8)
                {
                    reactTried++;
                    Print($"REACT TEST: it did NOT jump. {ReactWhen()} (already {F(reactLevelY - reactPressY)} below the edge, " +
                          $"frame time {reactFrameMs:0} ms). Tally at this setting: {reactJumped} of {reactTried} jumped.");
                    reactState = 0;
                    reactStable = 0;
                }

                break;
        }

        reactPrevY = y;
        reactPrevTime = now;
    }

    private string ReactWhen() =>
        reactPressTime - reactFallTime < 0.0005
            ? "Pressed on the SAME frame the drop first showed."
            : $"Pressed {(reactPressTime - reactFallTime) * 1000:0} ms after the drop first showed.";

    // ------------------------------------------------------------------
    // Things the options window calls
    // ------------------------------------------------------------------
    internal void TestJump() => Print($"Test jump sent. Game accepted it: {DoJump()}");

    // Asks the game's collision what is under your feet, once per filter, and prints the answers.
    internal void RunProbe()
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            Print("Probe: no character found.");
            return;
        }

        var pos = player.Position;
        Print($"PROBE at X {F(pos.X)}  Y {F(pos.Y)}  Z {F(pos.Z)}. Your hitbox radius is {F(HitboxRadius)}. Rays straight down through your feet, one line per filter:");
        foreach (var line in Collision.Probe(pos))
        {
            Print(line);
            log.Information(line);
        }
    }

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

            case "learn":
                if (IsLearning)
                    CancelLearning();
                else
                    StartLearning();
                break;

            case "margin":
                if (parts.Length > 1 && parts[1].ToLowerInvariant() == "apply")
                {
                    if (marginCount < 1 || HitboxRadius < 0.05f)
                    {
                        Print("Nothing to apply yet. Run /ledgejump margin, walk off a few ledges, then type /ledgejump margin apply.");
                    }
                    else
                    {
                        var scale = Math.Clamp(marginSum / marginCount / HitboxRadius, 0.5f, 1.5f);
                        config.AutoMargin = true;
                        config.MarginScale = scale;
                        Save();
                        Print($"Applied: the automatic margin is now {F(scale)} times your hitbox radius " +
                              $"({F(scale * HitboxRadius)} yalms), from {marginCount} measurement(s).");
                    }

                    break;
                }

                if (marginTest)
                {
                    marginTest = false;
                    Print("Margin measuring OFF. Everything is back to normal.");
                }
                else
                {
                    marginTest = true;
                    marginStable = 0;
                    marginHadLevel = false;
                    marginHaveEdge = false;
                    marginCount = 0;
                    marginSum = 0f;
                    Print($"Margin measuring ON (your hitbox radius is {F(HitboxRadius)}). Walk off ledges on foot, WITHOUT jumping, at your normal pace. " +
                          "Each time it prints how far past the end of the collision you stayed standing. " +
                          "Other features are paused. Type /ledgejump margin again to turn it off.");
                }

                break;

            case "probe":
                RunProbe();
                break;

            case "reacttest":
                if (reactTest && parts.Length < 2)
                {
                    reactTest = false;
                    Print("React test OFF. Everything is back to normal.");
                }
                else
                {
                    reactDelay = parts.Length > 1 && int.TryParse(parts[1], out var delay) ? Math.Clamp(delay, 0, 30) : 0;
                    reactTest = true;
                    reactState = 0;
                    reactStable = 0;
                    reactHadLevel = false;
                    reactTried = 0;
                    reactJumped = 0;
                    reactPrevTime = 0;
                    Print($"React test ON (jump pressed {(reactDelay == 0 ? "on the SAME frame" : reactDelay + " frame(s) after")} the drop first shows). " +
                          "Walk off a ledge on foot, WITHOUT jumping. Other features are paused. " +
                          "Type /ledgejump reacttest to turn it off.");
                }
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
        Print("/ledgejump margin apply  - after measuring, set the automatic margin from your measurements");
        Print("/ledgejump margin  - measure how far past the end of the collision you can keep standing (walk off ledges by hand)");
        Print("/ledgejump probe  - print what the game's collision sees under your feet (stand on the thing first)");
        Print("/ledgejump reacttest [n]  - experiment: press jump n frames after you walk off an edge (0 = same frame)");
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
