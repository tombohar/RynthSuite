// CraftingTab.cs - The Mini Remote's universal "Crafting" tab (every world, like Quests and Pets).
//
// UB-IT's crafting benches, one sub-tab each: Armor, Weapons (Melee / Missile / Caster),
// Jewelry, Trinkets and Items tinkering. (Infinite Leaftide's own crafting - chunking / clap,
// split arrows, the Rings inscription chain - belongs in RynthILT.) Every tinker tab has two
// sections:
//   * Retail tinkering - stock ACE salvage (TinkerCatalog.Retail), chance from TinkerMath;
//   * Server tinkering - the connected server's own salvage, from the plugin that owns it (its
//     "RynthServer.Profile" JSON's "salvage" list, MiniRemoteContext.ServerSalvage; RynthILT's on
//     Infinite Leaftide), then the player's own rows for this world (CraftingTabState.CustomByWorld).
//     With none defined it says so.
// One TinkerRunner works one tab's scan at a time.
// Above the benches: "Auto-confirm crafting" (CraftAutoConfirm), Yes to the server's chance prompt
// at the chosen percent for crafts the player starts by hand.
// The Kits sub-tab: "Make kits" (KitRunner / KitPlanner) makes Plentiful Healing Kits from their
// ingredients, one combine at a time, only while the macro is off (also /ra kits). Under it,
// "Make kits when low" (KitAuto; the macro side is RynthAiPlugin.Kits.cs) does the same by itself
// while the macro runs, when the kits carried fall under the line and nothing is around.
using System;
using System.Collections.Generic;
using System.Linq;
using ImGuiNET;
using RynthCore.Plugin.RynthAi.MiniRemote.Crafting;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAi.MiniRemote;

internal sealed class CraftingTab
{
    private readonly MiniRemoteContext _ctx;

    public TinkerRunner Runner { get; }

    /// <summary>Auto-confirm crafting (the sticky engine answer for hand-made crafts).</summary>
    public CraftAutoConfirm AutoConfirm { get; }

    /// <summary>Make kits (the Kits sub-tab, /ra kits).</summary>
    public KitRunner Kits { get; }

    /// <summary>Make kits when low: when a run may start with the macro on, and the waits (ticked by RynthAiPlugin.Kits.cs).</summary>
    public KitAuto AutoKits { get; }

    /// <summary>A command asked for the Kits sub-tab (the next frame selects it).</summary>
    private volatile bool _selectKits;
    public void SelectKitsTab() => _selectKits = true;
    private static readonly byte[] KitsTabLabel = System.Text.Encoding.UTF8.GetBytes("Kits\0");

    // Custom-row editor (render thread only)
    private int _newMaterial = TinkerCatalog.Steel;
    private int _newKind;
    private bool _newSlot = true;
    private string _newNameMatch = string.Empty;
    private string _newNote = string.Empty;

    private static readonly string[] KindLabels = { "Tink (repeatable)", "Imbue (one per item)", "Once (flag recipe)" };

    public CraftingTab(MiniRemoteContext ctx)
    {
        _ctx = ctx;
        Runner = new TinkerRunner(ctx, RowsFor, SettingsFor);
        AutoConfirm = new CraftAutoConfirm(() => ctx.Host, () => ctx.Settings, ctx.Chat);
        Kits = new KitRunner(ctx);
        AutoKits = new KitAuto { Say = ctx.Chat };
    }

    private CraftingTabState C => _ctx.State.Crafting;

    /// <summary>The bench's settings block (created on first use).</summary>
    private TinkerBenchSettings SettingsFor(TinkerBench bench)
    {
        var all = C.Benches;
        lock (all)
        {
            string key = bench.ToString();
            if (!all.TryGetValue(key, out var s) || s == null) all[key] = s = new TinkerBenchSettings();
            s.Counts ??= new Dictionary<string, int>();
            return s;
        }
    }

    /// <summary>The player's rows for the connected world (a copy).</summary>
    private List<CustomTinker> CustomRows()
    {
        string world = _ctx.WorldName;
        var map = C.CustomByWorld;
        lock (map)
            return map.TryGetValue(world, out var list) && list != null ? new List<CustomTinker>(list) : new List<CustomTinker>();
    }

    /// <summary>Retail + server + custom rows for a bench on this world.</summary>
    private IReadOnlyList<TinkerMaterial> RowsFor(TinkerBench bench)
        => TinkerPlanner.Rows(bench, ServerRowsFor(bench), CustomRows());

    /// <summary>The connected server's own salvage for a bench: what its plugin's profile defines.</summary>
    private IReadOnlyList<TinkerMaterial> ServerRowsFor(TinkerBench bench)
        => TinkerCatalog.FromServerPlugin(bench, _ctx.ServerSalvage);

    /// <summary>The connected server defines salvage for at least one bench.</summary>
    private bool ServerHasSalvage => _ctx.ServerSalvage.Count > 0;

    // ── Lifecycle (MiniRemoteController) ─────────────────────────────────────────────────────────

    public void Tick(long nowMs)
    {
        Runner.Tick(nowMs);
        AutoConfirm.Tick(nowMs);
        Kits.Tick(nowMs);
    }

    public void OnChat(string text)
    {
        Runner.OnChat(text);
        Kits.OnChat(text);
    }

    public void OnLogout()
    {
        Runner.OnLogout();
        Kits.OnLogout();
        AutoKits.Reset();
        AutoConfirm.Release();
    }

    // ── UI (render thread) ──────────────────────────────────────────────────

    public void Render()
    {
        RenderAutoConfirm();
        if (!ImGui.BeginTabBar("##crafting")) return;
        if (ImGui.BeginTabItem("Armor")) { RenderBench(TinkerBench.Armor); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Weapons"))
        {
            if (ImGui.BeginTabBar("##craftweapons"))
            {
                if (ImGui.BeginTabItem("Melee")) { RenderBench(TinkerBench.Melee); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Missile")) { RenderBench(TinkerBench.Missile); ImGui.EndTabItem(); }
                if (ImGui.BeginTabItem("Caster")) { RenderBench(TinkerBench.Caster); ImGui.EndTabItem(); }
                ImGui.EndTabBar();
            }
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Jewelry")) { RenderBench(TinkerBench.Jewelry); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Trinkets")) { RenderBench(TinkerBench.Trinkets); ImGui.EndTabItem(); }
        if (ImGui.BeginTabItem("Items")) { RenderBench(TinkerBench.Items); ImGui.EndTabItem(); }
        bool selectKits = _selectKits;
        _selectKits = false;
        if (BeginTab(KitsTabLabel, selectKits ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None)) { RenderKits(); ImGui.EndTabItem(); }
        ImGui.EndTabBar();
    }

    /// <summary>BeginTabItem with flags and no close button (ImGui.NET only wraps flags with p_open).</summary>
    private static unsafe bool BeginTab(byte[] labelUtf8Z, ImGuiTabItemFlags flags)
    {
        fixed (byte* label = labelUtf8Z)
            return ImGuiNative.igBeginTabItem(label, null, flags) != 0;
    }

    // ── Kits (render thread) ────────────────────────────────────────────────

    /// <summary>The Kits sub-tab: what the pack makes, the floor and count, Make kits / Stop.</summary>
    private void RenderKits()
    {
        var k = Kits;
        k.MarkViewed(MiniRemoteContext.NowMs);
        SafeImGui.TextWrapped("Makes kits from their ingredients, one combine at a time, waiting for each result. "
                              + "Every combine uses your Healing skill and can fail: a failure destroys one of each item. "
                              + "Runs only while the macro is off, and stops if you move, leave peace mode or take damage.");
        ImGui.Spacing();

        int floor = Math.Clamp(C.KitMinChancePercent, 0, 100);
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Min chance %##kitfloor", ref floor, 5))
        {
            int value = Math.Clamp(floor, 0, 100);
            _ctx.Post(() => C.KitMinChancePercent = value);
        }
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Never try a combine whose chance is under this (0 = any chance).\n"
                                 + "Worked out from your buffed Healing skill before every combine; when AC asks,\n"
                                 + "RynthAi also says No to a lower server percent. Also /ra kits floor <0-100>.");
        ImGui.SameLine();
        int count = Math.Max(0, C.KitMakeCount);
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Kits to make##kitcount", ref count, 1))
        {
            int value = Math.Clamp(count, 0, 1000);
            _ctx.Post(() => C.KitMakeCount = value);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("How many kits one run makes (0 = as many as the pack makes).");

        var plan = k.Plan;
        if (plan.Length == 0)
        {
            SafeImGui.TextDisabled("Reading your pack...");
        }
        else if (ImGui.BeginTable("##kits", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Kit", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("Can make", ImGuiTableColumnFlags.WidthFixed, 65);
            ImGui.TableSetupColumn("In your pack");
            ImGui.TableSetupColumn("Chance");
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();
            bool macroOn = _ctx.Settings?.IsMacroRunning == true;
            foreach (var row in plan)
            {
                ImGui.PushID(row.Recipe.Id);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.Recipe.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(row.CanMake.ToString());
                ImGui.TableNextColumn();
                SafeImGui.TextWrapped(KitPlanner.PartsText(row));
                ImGui.TableNextColumn();
                foreach (var c in row.Chances)
                {
                    string text = $"{c.Step.Label}: {(c.Percent >= 0 ? c.Percent + "%" : "?")}";
                    if (c.UnderFloor) SafeImGui.TextColored(LegacyDashboardRenderer.ColAmber, text + " (under the floor)");
                    else SafeImGui.TextDisabled(text);
                }
                ImGui.TableNextColumn();
                string why = macroOn ? "Turn the macro off first: kits are made only while it is off."
                    : k.Running ? "A kit run is going."
                    : row.Blocked;
                ImGui.BeginDisabled(why.Length > 0);
                if (ImGui.SmallButton("Make kits"))
                {
                    var recipe = row.Recipe;
                    int ask = Math.Max(0, C.KitMakeCount);
                    int n = ask > 0 ? Math.Min(ask, row.CanMake) : row.CanMake;
                    int fl = Math.Clamp(C.KitMinChancePercent, 0, 100);
                    _ctx.Confirm("Make kits",
                        $"Make up to {n} {recipe.Name}(s)?\n\nChance: {KitPlanner.ChancesText(row)} (floor {fl}%).\n"
                        + "A failed combine destroys one of each item it used.\n"
                        + "It stops if you move, leave peace mode, take damage or turn the macro on.",
                        () =>
                        {
                            string refused = Kits.Start(recipe, ask);
                            if (refused.Length > 0) _ctx.Chat("[RynthAi] Make kits: can't start - " + refused + ".");
                        }, "Make kits");
                }
                ImGui.EndDisabled();
                if (why.Length > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) SafeImGui.SetTooltip(why);
                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        ImGui.BeginDisabled(!k.Running);
        if (ImGui.Button("Stop##kitstop")) _ctx.Post(() => Kits.Stop("stopped by you"));
        ImGui.EndDisabled();
        ImGui.SameLine();
        SafeImGui.TextDisabled(k.Status);
        if (k.LastSummary.Length > 0) SafeImGui.TextWrapped("Last run: " + k.LastSummary);
        RenderAutoKits();
        SafeImGui.TextDisabled("Ingredients: Soft Bandages, Treated Hyssop and Treated Mandrake (Healing must be trained). Also /ra kits.");
    }

    /// <summary>Make kits when low: the checkbox, the two numbers and what it is doing.</summary>
    private void RenderAutoKits()
    {
        var settings = _ctx.Settings;
        if (settings == null) return;
        ImGui.Separator();
        bool on = settings.MakeKitsWhenLow;
        if (ImGui.Checkbox("Make kits when low##kitauto", ref on))
        {
            bool value = on;
            _ctx.Post(() =>
            {
                settings.MakeKitsWhenLow = value;
                if (value) AutoKits.LookNow();
                else
                {
                    if (Kits.Auto) Kits.Stop("Make kits when low turned off", KitStopKind.Other);
                    AutoKits.Off();
                }
                _ctx.SaveCombatSettings?.Invoke();
            });
        }
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("While the macro runs: when you carry fewer Plentiful Healing Kits than the first number,\n"
                                 + "make up to the second number from your pack, at the Min chance % above.\n"
                                 + $"Only when nothing is around: no monster within {KitAuto.DefaultRadiusYd:0} yd (or your Monster Range, if bigger),\n"
                                 + $"no hit for {KitAuto.HurtQuietMs / 1000} s, no healing, buffing, looting or fight. The macro waits meanwhile;\n"
                                 + "a monster, a hit, a heal or moving stops it at once. Also /ra kits auto on|off.");
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("when fewer than");
        ImGui.SameLine();
        int below = Math.Clamp(settings.MakeKitsBelow, 1, KitAuto.MaxBelow);
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("##kitbelow", ref below, 1))
        {
            int value = Math.Clamp(below, 1, KitAuto.MaxBelow);
            _ctx.Post(() => { settings.MakeKitsBelow = value; _ctx.SaveCombatSettings?.Invoke(); });
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"A run starts when you carry fewer kits than this (1-{KitAuto.MaxBelow}). Also /ra kits auto below N.");
        ImGui.SameLine();
        ImGui.TextUnformatted("kits, make up to");
        ImGui.SameLine();
        int upTo = Math.Clamp(settings.MakeKitsUpTo, 1, KitAuto.MaxUpTo);
        ImGui.SetNextItemWidth(80);
        if (ImGui.InputInt("##kitupto", ref upTo, 1))
        {
            int value = Math.Clamp(upTo, 1, KitAuto.MaxUpTo);
            _ctx.Post(() => { settings.MakeKitsUpTo = value; _ctx.SaveCombatSettings?.Invoke(); });
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"The most kits one run makes (1-{KitAuto.MaxUpTo}); fewer when the pack runs out. Also /ra kits auto upto M.");
        if (!settings.MakeKitsWhenLow) SafeImGui.TextDisabled("Make kits when low is off.");
        else SafeImGui.TextDisabled("Make kits when low: " + AutoStatusText(settings));
    }

    /// <summary>What Make kits when low is doing ("running 2/5", "waiting: monster 22 yd away; next check in 4 s", ...).</summary>
    public string AutoStatusText(LegacyUiSettings settings)
    {
        if (!settings.MakeKitsWhenLow) return "off";
        if (Kits.Auto) return $"running {Kits.MadeCount}/{Math.Max(1, Kits.Limit)}";
        if (!settings.IsMacroRunning) return "waiting: the macro is off";
        return AutoKits.Status;
    }

    /// <summary>Auto-confirm crafting: the checkbox, the percent and what it has done.</summary>
    private void RenderAutoConfirm()
    {
        var settings = _ctx.Settings;
        if (settings == null) return;
        bool supported = AutoConfirm.Supported;
        bool on = settings.AutoConfirmCraft && supported;
        ImGui.BeginDisabled(!supported);
        if (ImGui.Checkbox("Auto-confirm crafting##autoconfirm", ref on))
        {
            bool value = on;
            _ctx.Post(() => { settings.AutoConfirmCraft = value; _ctx.SaveCombatSettings?.Invoke(); });
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SafeImGui.SetTooltip(supported
                ? "When you combine items yourself (salvage on an item, a crafting recipe), click Yes on the\n"
                  + "server's \"N percent chance to succeed\" prompt when the chance is at least the percent here.\n"
                  + "A lower chance shows AC's dialog as usual. It never says No. Also /ra autoconfirm."
                : "Auto-confirm crafting " + CraftAutoConfirm.NeedsEngineText + ".");
        ImGui.SameLine();
        ImGui.BeginDisabled(!supported || !settings.AutoConfirmCraft);
        int percent = CraftAutoConfirm.ClampPercent(settings.AutoConfirmCraftPercent);
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("% or better##autoconfirmpct", ref percent, 5))
        {
            int value = CraftAutoConfirm.ClampPercent(percent);
            _ctx.Post(() => { settings.AutoConfirmCraftPercent = value; _ctx.SaveCombatSettings?.Invoke(); });
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip($"The lowest chance to say Yes to ({CraftAutoConfirm.MinThreshold}-{CraftAutoConfirm.MaxThreshold}). Below it AC's dialog shows.");
        ImGui.SameLine();
        if (!supported)
            SafeImGui.TextDisabled("needs a RynthCore update");
        else if (settings.AutoConfirmCraft && AutoConfirm.AcceptedCount > 0)
            SafeImGui.TextDisabled($"{AutoConfirm.AcceptedCount} confirmed this session");
        else
            SafeImGui.TextDisabled(settings.AutoConfirmCraft ? AutoConfirm.Status : "");
        ImGui.Separator();
    }

    private void RenderBench(TinkerBench bench)
    {
        var s = SettingsFor(bench);
        string world = _ctx.WorldName;
        bool weapon = bench is TinkerBench.Melee or TinkerBench.Missile or TinkerBench.Caster;
        ImGui.PushID((int)bench);

        RenderFilters(s, weapon);
        if (bench == TinkerBench.Trinkets) RenderTrinketOptions();

        if (ImGui.CollapsingHeader("Retail tinkering", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var retail = TinkerCatalog.RetailFor(bench);
            if (retail.Count == 0) ImGui.TextDisabled("Retail AC has no tinkering for this tab.");
            else RenderRows(s, retail, "retail");
        }

        // The server plugin's name when RynthCore has an active server, else the world name.
        string server = _ctx.ServerName.Length > 0 ? _ctx.ServerName : world.Length > 0 ? world : "this server";
        string serverTitle = $"Server tinkering - {server}###server";
        if (ImGui.CollapsingHeader(serverTitle, ImGuiTreeNodeFlags.DefaultOpen))
        {
            var serverRows = ServerRowsFor(bench);
            if (serverRows.Count > 0)
            {
                RenderRows(s, serverRows, "server");
                if (serverRows.Any(r => !r.UsesTinkSlot && r.AppliedInt == 0))
                    ImGui.TextDisabled("Rows without a tink slot don't show on the item: a run applies each once, and items\n"
                                       + "finished this session are left out of later scans (Forget brings them back).");
            }
            else if (ServerHasSalvage)
                ImGui.TextDisabled("This server doesn't have any dynamic salvage defined for this tab.");
            else
                ImGui.TextDisabled("This server doesn't have any dynamic salvage defined.");
            RenderCustom(bench, s, world);
        }

        RenderRun(bench, s);
        ImGui.PopID();
    }

    private static void RenderFilters(TinkerBenchSettings s, bool weapon)
    {
        int min = s.MinChancePercent;
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Min chance %", ref min, 5)) s.MinChancePercent = Math.Clamp(min, 0, 100);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Skip a salvage whose chance is under this (0 = any).\nRetail chances are worked out ahead; "
                             + "the server's own percent is checked when it asks.");
        ImGui.SameLine();
        int maxT = s.MaxTinks;
        ImGui.SetNextItemWidth(90);
        if (ImGui.SliderInt("Max tinks", ref maxT, 0, TinkerCatalog.MaxTinks)) s.MaxTinks = maxT;
        ImGui.SameLine();
        int minFree = s.MinFreeTinks;
        ImGui.SetNextItemWidth(90);
        if (ImGui.SliderInt("Min free", ref minFree, 0, TinkerCatalog.MaxTinks)) s.MinFreeTinks = minFree;
        bool skipImbued = s.SkipImbued;
        if (ImGui.Checkbox("Skip imbued", ref skipImbued)) s.SkipImbued = skipImbued;
        if (weapon)
        {
            ImGui.SameLine();
            bool pierce = s.PierceForCombo;
            if (ImGui.Checkbox("Slash/pierce: pierce rend", ref pierce)) s.PierceForCombo = pierce;
            ImGui.SameLine();
            bool rendLast = s.RendLast;
            if (ImGui.Checkbox("Rend last", ref rendLast)) s.RendLast = rendLast;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Apply the rend after everything else (a Foolproof rend can't fail).");
        }
    }

    /// <summary>Trinkets: the prepared salvage tier and whether to prepare raw bags on the tier's Ammunition.</summary>
    private void RenderTrinketOptions()
    {
        int tier = Math.Clamp(C.TrinketTier, 0, TinkerCatalog.TrinketTiers.Length - 1);
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("Tier", ref tier, TinkerCatalog.TrinketTiers, TinkerCatalog.TrinketTiers.Length)) C.TrinketTier = tier;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Uses bags whose name has this tier (e.g. \"Major\"), made on that tier's Ammunition.");
        ImGui.SameLine();
        bool prep = C.TrinketPrep;
        if (ImGui.Checkbox("Prepare on Ammunition", ref prep)) C.TrinketPrep = prep;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("With no bag of the tier carried, use a full raw bag on the tier's Ammunition first,\n"
                             + "then apply the prepared bag.");
        ImGui.TextDisabled("Trinkets: compass, goggles, scarab, watch, puzzle box, top (the Jewelry tab leaves them out).");
    }

    /// <summary>One ticked count / checkbox per row.</summary>
    private static void RenderRows(TinkerBenchSettings s, IReadOnlyList<TinkerMaterial> rows, string id)
    {
        if (!ImGui.BeginTable("##rows" + id, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp)) return;
        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("Salvage", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("Effect");
        foreach (var row in rows)
        {
            string key = TinkerCatalog.Key(row);
            ImGui.PushID(key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            CountControl(s, key, row.Kind);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);
            ImGui.TableNextColumn();
            string slot = row.UsesTinkSlot ? "" : " - no tink slot";
            SafeImGui.TextDisabled(row.Kind == TinkerKind.Imbue ? row.Effect + " (imbue)" : row.Effect + (row.Effect.Contains("tink slot") ? "" : slot));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    /// <summary>Tinks: how many (0-10); imbues and one-shot recipes: on/off.</summary>
    private static void CountControl(TinkerBenchSettings s, string key, TinkerKind kind)
    {
        int count;
        lock (s.Counts) count = s.Counts.TryGetValue(key, out int c) ? c : 0;
        if (kind == TinkerKind.Tink)
        {
            ImGui.SetNextItemWidth(70);
            if (ImGui.SliderInt("##n", ref count, 0, TinkerCatalog.MaxTinks)) SetCount(s, key, count);
        }
        else
        {
            bool on = count > 0;
            if (ImGui.Checkbox("##on", ref on)) SetCount(s, key, on ? 1 : 0);
        }
    }

    private static void SetCount(TinkerBenchSettings s, string key, int count)
    {
        lock (s.Counts)
        {
            if (count <= 0) s.Counts.Remove(key);
            else s.Counts[key] = count;
        }
    }

    /// <summary>The player's own rows for this world and bench, plus the add form.</summary>
    private void RenderCustom(TinkerBench bench, TinkerBenchSettings s, string world)
    {
        ImGui.Spacing();
        ImGui.TextColored(LegacyDashboardRenderer.ColTeal, "Your salvage for this server");
        if (world.Length == 0) { ImGui.TextDisabled("Log in to add rows for this server."); return; }

        var mine = CustomRows().Where(c => c.Bench == (int)bench).ToList();
        var rows = TinkerPlanner.Rows(bench, null, mine).Where(r => r.Origin == TinkerOrigin.Custom).ToList();
        if (rows.Count == 0) ImGui.TextDisabled("None yet.");
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var src = mine[i];
            string key = TinkerCatalog.Key(row);
            ImGui.PushID("custom" + i);
            CountControl(s, key, row.Kind);
            ImGui.SameLine();
            string kind = row.Kind switch { TinkerKind.Imbue => "imbue", TinkerKind.Once => "once", _ => "tink" };
            string extra = (row.UsesTinkSlot ? "" : ", no tink slot") + (row.NameMatch.Length > 0 ? $", bag name has \"{row.NameMatch}\"" : "");
            ImGui.TextUnformatted($"{row.Name} ({kind}{extra}) - {row.Effect}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                var victim = src;
                _ctx.Post(() =>
                {
                    var map = C.CustomByWorld;
                    lock (map)
                        if (map.TryGetValue(world, out var list) && list != null) list.Remove(victim);
                });
            }
            ImGui.PopID();
        }

        // Add form
        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("##newmat", TinkerCatalog.MaterialName(_newMaterial)))
        {
            foreach (var (mid, name) in TinkerCatalog.MaterialNames.OrderBy(m => m.Name))
                if (ImGui.Selectable(name, mid == _newMaterial)) _newMaterial = mid;
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("##newkind", ref _newKind, KindLabels, KindLabels.Length);
        ImGui.SameLine();
        ImGui.Checkbox("Uses a tink slot", ref _newSlot);
        ImGui.SetNextItemWidth(150);
        ImGui.InputTextWithHint("##newmatch", "bag name contains (optional)", ref _newNameMatch, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##newnote", "effect note", ref _newNote, 96);
        ImGui.SameLine();
        if (ImGui.SmallButton("Add"))
        {
            var add = new CustomTinker
            {
                Bench = (int)bench,
                Material = _newMaterial,
                Imbue = _newKind == 1,
                Once = _newKind == 2,
                UsesTinkSlot = _newSlot,
                NameMatch = _newNameMatch.Trim(),
                Note = _newNote.Trim(),
            };
            _ctx.Post(() =>
            {
                var map = C.CustomByWorld;
                lock (map)
                {
                    if (!map.TryGetValue(world, out var list) || list == null) map[world] = list = new List<CustomTinker>();
                    list.Add(add);
                }
            });
            _newNote = string.Empty;
        }
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("Adds a row for this server only. Its chance isn't known ahead:\nthe server's own percent decides (Min chance %).");
    }

    /// <summary>Scan / Start / Stop, the shared run options and the scanned rows.</summary>
    private void RenderRun(TinkerBench bench, TinkerBenchSettings s)
    {
        ImGui.Separator();
        bool canAnswer = _ctx.Host.HasConfirmationAnswers;
        bool answer = C.AnswerCraftDialog && canAnswer;
        ImGui.BeginDisabled(!canAnswer);
        if (ImGui.Checkbox("Answer AC's craft dialog", ref answer)) C.AnswerCraftDialog = answer;
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            SafeImGui.SetTooltip(canAnswer
                ? "Says Yes for you only when the server's chance reaches Min chance %, else No."
                : "Needs RynthCore engine API v81: until then click Yes in AC's dialog yourself.");
        ImGui.SameLine();
        int batch = C.BatchSize;
        ImGui.SetNextItemWidth(90);
        if (ImGui.InputInt("Batch", ref batch, 5)) C.BatchSize = Math.Clamp(batch, 1, 100);
        ImGui.SameLine();
        bool combat = C.AllowInCombat;
        if (ImGui.Checkbox("Allow in combat mode", ref combat)) C.AllowInCombat = combat;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only for servers that allow crafting outside peace mode.");

        bool autoNext = C.AutoNextBatch;
        if (ImGui.Checkbox("Auto next batch", ref autoNext)) C.AutoNextBatch = autoNext;
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("When a batch finishes, scan the next items (finished ones are left out).\n"
                             + "Stops after a batch that used no bag.");
        ImGui.SameLine();
        ImGui.BeginDisabled(!autoNext);
        bool autoStart = C.AutoStartNextBatch;
        if (ImGui.Checkbox("and start it", ref autoStart)) C.AutoStartNextBatch = autoStart;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Start the next batch without asking again (off: press Start yourself).");
        ImGui.EndDisabled();
        int doneCount = Runner.DoneCount(bench);
        if (doneCount > 0)
        {
            ImGui.SameLine();
            SafeImGui.TextDisabled($"{doneCount} finished this session");
            ImGui.SameLine();
            ImGui.BeginDisabled(Runner.Running);
            if (ImGui.SmallButton("Forget")) _ctx.Post(() => Runner.ForgetDone(bench));
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Let finished items show in scans again (server rows that don't show on the item could go on twice).");
        }

        bool running = Runner.Running;
        bool mine = Runner.ScannedBench == (int)bench;
        ImGui.BeginDisabled(running);
        if (ImGui.Button("Scan")) _ctx.Post(() => Runner.Scan(bench));
        ImGui.SameLine();
        ImGui.BeginDisabled(!mine || Runner.Rows.Length == 0);
        if (ImGui.Button("Start"))
        {
            int n = Runner.Rows.Length;
            string answerText = C.AnswerCraftDialog && canAnswer
                ? (s.MinChancePercent > 0 ? $"RynthAi answers AC's dialog: Yes at {s.MinChancePercent}% or better." : "RynthAi answers Yes to AC's dialog.")
                : "AC asks you to confirm every craft.";
            _ctx.Confirm($"{TinkerCatalog.Label(bench)} tinkering",
                $"Apply the planned salvage to {n} item(s)?\n\nTinkering can fail, and every attempt uses the bag and a tink slot.\n{answerText}",
                () => Runner.Start(bench), "Start");
        }
        ImGui.EndDisabled();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!running);
        if (ImGui.Button("Stop")) _ctx.Post(() => Runner.Stop("stopped by player"));
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (running && Runner.Bench != bench)
            SafeImGui.TextColored(LegacyDashboardRenderer.ColAmber, $"{TinkerCatalog.Label(Runner.Bench)} is running");
        else
            SafeImGui.TextDisabled(Runner.Status);

        if (!mine) return;
        var scanned = Runner.Rows;
        if (scanned.Length == 0) return;
        if (!ImGui.BeginTable("##scan", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY,
                new System.Numerics.Vector2(0, 200)))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Tinks", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Work", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("Plan");
        ImGui.TableSetupColumn("Status");
        ImGui.TableHeadersRow();
        foreach (var r in scanned)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Imbued ? r.Name + " *" : r.Name);
            if (r.Imbued && ImGui.IsItemHovered()) ImGui.SetTooltip("Imbued");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Tinks.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Workmanship.ToString("0"));
            ImGui.TableNextColumn();
            SafeImGui.TextWrapped(r.Plan);
            ImGui.TableNextColumn();
            SafeImGui.TextWrapped(r.Status);
        }
        ImGui.EndTable();
    }
}
