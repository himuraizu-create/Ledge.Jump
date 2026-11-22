using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;

namespace LedgeJump;

// The options window. Open it with /ledgejump or the settings button in the plugin installer.
internal sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private readonly Configuration config;
    private readonly Action save;

    private string newName = "";

    public ConfigWindow(Plugin plugin, Configuration config, Action save)
        : base("Ledge Jump###LedgeJumpConfig")
    {
        this.plugin = plugin;
        this.config = config;
        this.save = save;

        Size = new Vector2(500, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##ledgejumptabs"))
            return;

        if (ImGui.BeginTabItem("General"))
        {
            DrawGeneral();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Jump Spots"))
        {
            DrawSpots();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Auto Edge"))
        {
            DrawAutoEdge();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    // ------------------------------------------------------------------
    // General tab
    // ------------------------------------------------------------------
    private void DrawGeneral()
    {
        ImGui.Spacing();
        Check("Plugin enabled", config.Enabled, v => config.Enabled = v,
            "Master switch. Nothing jumps while this is off.");
        Check("Use recorded jump spots", config.UseSpots, v => config.UseSpots = v,
            "Jump when you run onto a spot from the Jump Spots tab.");
        Check("Use automatic edge detection", config.UseAutoEdge, v => config.UseAutoEdge = v,
            "Jump at the edge of whatever you are standing on, found from the game's collision. See the Auto Edge tab.");
        Check("Chat messages when it jumps", config.Debug, v => config.Debug = v,
            "Prints a line in chat for each jump. Handy while tuning, turn off once it feels right.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button("Test jump"))
            plugin.TestJump();
        Tip("Makes your character jump once, to check the jump action works.");

        ImGui.Spacing();
        ImGui.TextWrapped("Tip: Ctrl + click on any slider lets you type an exact number.");
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"Current zone ID: {plugin.CurrentTerritory}");
    }

    // ------------------------------------------------------------------
    // Jump Spots tab
    // ------------------------------------------------------------------
    private void DrawSpots()
    {
        ImGui.Spacing();
        ImGui.Text("Learn an edge (best for thin or odd platforms)");
        ImGui.TextWrapped(
            "Click Learn, then walk off the edge by hand (do not jump). The plugin watches your own height, " +
            "notices the moment you drop, and records the last place you stood. It does not need the game's collision data.");
        ImGui.Spacing();

        if (plugin.IsLearning)
        {
            if (ImGui.Button("Cancel learning"))
                plugin.CancelLearning();
        }
        else if (ImGui.Button("Learn an edge"))
        {
            plugin.StartLearning();
        }

        if (!string.IsNullOrEmpty(plugin.LearnStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(plugin.IsLearning ? ImGuiColors.DalamudYellow : ImGuiColors.DalamudGrey,
                plugin.LearnStatus);
        }

        Slider("Standing height tolerance", config.LearnTolerance, 0.002f, 0.05f, v => config.LearnTolerance = v,
            "How much your height may change and still count as 'still standing' while learning.\n" +
            "Smaller = records closer to the true edge, but too small can miss on slightly uneven ground.\n" +
            "If a learned spot is always a bit too far out, lower this.",
            "%.3f");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Record by hand");
        ImGui.TextWrapped("Or stand at the very tip of a platform and click Record. Running at that spot makes you jump.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200f);
        ImGui.InputTextWithHint("##newname", "Name (optional)", ref newName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Record spot here"))
        {
            plugin.AddSpot(newName);
            newName = "";
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        Slider("Jump point (yalms before the spot)", config.ZoneRadius, 0f, 1f, v => config.ZoneRadius = v,
            "How far BEFORE the recorded spot the jump is pressed.\n" +
            "Smaller = jumps later, closer to the edge.\n" +
            "Bigger = jumps earlier. Raise it if you fall off before the jump happens.");
        Slider("Sideways leeway", config.SideLeeway, 0.05f, 3f, v => config.SideLeeway = v,
            "How far to the left or right of the spot you can be and still count.\n" +
            "Bigger = more forgiving if you do not run perfectly straight.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Auto-tune");
        ImGui.TextWrapped(
            "After every jump at a spot, the plugin measures where you actually left the ground. If you left too early it " +
            "jumps a little later next time. If you fell off before the jump took effect, it jumps a little earlier. " +
            "Run at the spot several times at the speed you will really use.");
        ImGui.Spacing();

        Check("Auto-tune jump timing", config.AutoTune, v => config.AutoTune = v,
            "Adjusts each spot's timing offset after every jump. Turn off to tune by hand with the offset sliders below.");
        Slider("Tuning strength", config.TuneStrength, 0.1f, 1f, v => config.TuneStrength = v,
            "How much of the measured error to correct each time.\n" +
            "Low = slow and steady. High = fast but can swing back and forth.");
        Slider("Jump earlier after a fall (yalms)", config.FailStep, 0.02f, 0.3f, v => config.FailStep = v,
            "How much earlier to jump after you fell off the edge before the jump took effect.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (config.Spots.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No spots yet.");
            return;
        }

        ImGui.Text($"Your spots ({config.Spots.Count})");
        ImGui.Spacing();

        var removeAt = -1;
        for (var i = 0; i < config.Spots.Count; i++)
        {
            var spot = config.Spots[i];
            ImGui.PushID(i);

            var name = spot.Name;
            ImGui.SetNextItemWidth(210f);
            if (ImGui.InputText("##name", ref name, 64))
                spot.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit())
                save();
            Tip("Click and type to rename.");

            ImGui.SameLine();
            if (ImGui.Button("Move here"))
                plugin.MoveSpotHere(i);
            Tip("Replace this spot's position with where you are standing right now.");

            ImGui.SameLine();
            if (ImGui.Button("Delete"))
                removeAt = i;

            var here = spot.Territory == plugin.CurrentTerritory ? "   (this zone)" : "";
            ImGui.TextColored(ImGuiColors.DalamudGrey,
                $"x {F(spot.X)}   z {F(spot.Z)}   zone {spot.Territory}{here}");

            // Optional back end of the platform
            if (ImGui.Button("Set back end here"))
                plugin.SetBackHere(i);
            Tip("Stand at the furthest-back point of the platform (the last place you can stand before falling off the\n" +
                "other side), then click this. The plugin then measures along the platform itself, which is steadier\n" +
                "than measuring along wherever you happen to be heading.");

            if (spot.HasBack)
            {
                var bx = spot.X - spot.BackX;
                var bz = spot.Z - spot.BackZ;
                ImGui.SameLine();
                if (ImGui.Button("Clear back end"))
                    plugin.ClearBack(i);
                ImGui.SameLine();
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"platform length {F(MathF.Sqrt(bx * bx + bz * bz))}");
            }

            // Timing offset, adjusted by the auto-tuner
            Slider("Timing offset", spot.Adjust, -0.5f, 1.5f, v => spot.Adjust = v,
                "Added to the global jump point for this spot only.\n" +
                "Positive = jump earlier. Negative = jump later.\n" +
                "The auto-tuner changes this after each jump. You can also set it by hand.");
            ImGui.SameLine();
            if (ImGui.Button("Reset tuning"))
                plugin.ResetTuning(i);
            Tip("Set the timing offset back to 0 and clear the attempt count.");

            if (spot.Attempts > 0)
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey, $"{spot.Attempts} jumps. Last: {spot.LastOutcome}");
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PopID();
        }

        if (removeAt >= 0)
            plugin.RemoveSpotAt(removeAt);

        // Log of recent jumps, for checking how well the timing is converging
        ImGui.Text("Recent jump results");
        var log = plugin.CalibrationLog;
        if (log.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "Nothing yet. Run at a spot.");
        }
        else
        {
            var first = Math.Max(0, log.Count - 12);
            for (var i = first; i < log.Count; i++)
                ImGui.TextWrapped(log[i]);

            if (ImGui.Button("Copy log"))
                ImGui.SetClipboardText(string.Join("\n", log));
            Tip("Copies the whole log so you can paste it somewhere.");
            ImGui.SameLine();
            if (ImGui.Button("Clear log"))
                plugin.ClearCalibrationLog();
        }
    }

    // ------------------------------------------------------------------
    // Auto Edge tab
    // ------------------------------------------------------------------
    private void DrawAutoEdge()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Looks at the game's real collision to find where the thing you are standing on ends, " +
            "and jumps at that edge as you walk toward it. No recording needed.");
        ImGui.Spacing();

        Check("Use automatic edge detection", config.UseAutoEdge, v => config.UseAutoEdge = v,
            "Same switch as on the General tab.");
        Check("Ignore walls", config.IgnoreWalls, v => config.IgnoreWalls = v,
            "Do not jump when a wall is what is ahead of you. Turn off only if it refuses to jump at a real ledge.");

        ImGui.Spacing();

        Slider("Jump point (yalms before the edge)", config.EdgeLead, -0.5f, 0.5f, v => config.EdgeLead = v,
            "How far BEFORE the real edge the jump is pressed.\n" +
            "Smaller or negative = later, closer to the edge.\n" +
            "Raise it if you fall off before the jump happens.");
        Slider("Frame-rate safety", config.EdgeWiden, 0.5f, 3f, v => config.EdgeWiden = v,
            "Higher = starts the jump a little earlier to make sure it is not missed on slow frames.\n" +
            "1.0 is the tightest. Raise it if it sometimes does not jump.");
        Slider("What counts as a drop (yalms)", config.DropThreshold, 0.3f, 5f, v => config.DropThreshold = v,
            "Ground lower than this below you counts as a fall. Small steps down are ignored.");
        Slider("Look-ahead distance (yalms)", config.ScanRange, 0.5f, 3f, v => config.ScanRange = v,
            "How far ahead of you it looks for an edge.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.Text("Live readout (walk toward an edge with this window open)");
        ImGui.Spacing();

        if (!plugin.HaveEdgeReading)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "No reading yet. Start walking.");
            return;
        }

        var r = plugin.LastEdge;
        if (!r.Grounded)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed,
                "The game's collision cannot see ground under you here (or you are in the air).");
            ImGui.TextWrapped("If you are standing on a normal platform and keep seeing this, the platform is not part of the " +
                              "collision this feature can read. Use \"Learn an edge\" on the Jump Spots tab for it instead. " +
                              "That works from your own movement and does not need the collision data.");
            return;
        }

        ImGui.TextColored(ImGuiColors.HealerGreen, $"Ground under you: yes (height {F(r.GroundY)})");

        if (!r.HasEdge)
            ImGui.Text($"Edge ahead: none within {F(config.ScanRange)} yalms");
        else
            ImGui.Text($"Edge ahead: {F(r.EdgeDistance)} yalms away");

        if (r.WallAhead)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "A wall is in the way, so it will not jump.");
    }

    // ------------------------------------------------------------------
    // Small helpers
    // ------------------------------------------------------------------
    private void Check(string label, bool current, Action<bool> set, string tip)
    {
        var v = current;
        if (ImGui.Checkbox(label, ref v))
        {
            set(v);
            save();
        }

        Tip(tip);
    }

    private void Slider(string label, float current, float min, float max, Action<float> set, string tip,
        string format = "%.2f")
    {
        var v = current;
        ImGui.SetNextItemWidth(220f);
        if (ImGui.SliderFloat(label, ref v, min, max, format))
            set(v);
        if (ImGui.IsItemDeactivatedAfterEdit())
            save();
        Tip(tip);
    }

    private static void Tip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private static string F(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
