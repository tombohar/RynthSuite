using System;
using System.IO;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.Meta;
using RynthCore.RynthAiHostTests.Tests;
using RynthCore.RynthAiTests.Harness;

namespace RynthCore.RynthAiHostTests;

// Offline tests for RynthAi's host-bound logic: loot rule matching and metas. Each test prints
// PASS / FAIL, or KNOWN for a registered real bug (which does not fail the run).
//
// Run: dotnet run -c Release                 (all tests)
//      dotnet run -c Release -- vtank        (tests whose name contains "vtank")
internal static class Program
{
    /// <summary>Scratch folder next to the exe for files tests write. Emptied at start.</summary>
    public static string TempRoot { get; } = Path.Combine(AppContext.BaseDirectory, "scratch");

    /// <summary>DungeonHazardStore's folder before Main pointed it at the scratch folder.</summary>
    public static string LiveHazardFolder { get; private set; } = "";

    /// <summary>CreatureProfileStore's folder before Main pointed it at the scratch folder.</summary>
    public static string LiveCreatureFolder { get; private set; } = "";

    /// <summary>DungeonHazardSurfaces' folder (HazardTextures.txt) before Main pointed it at the scratch folder.</summary>
    public static string LiveHazardTextureFolder { get; private set; } = "";

    private static int Main(string[] args)
    {
        if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true);
        Directory.CreateDirectory(TempRoot);

        // SAFETY: the expression engine reads and writes pvars/gvars (and reads ItemGiver
        // profiles) under C:\Games\RynthSuite\RynthAi. Point them at the scratch folder before
        // any engine exists. The "expr: safety" test checks this held.
        ExpressionEngine.PvarsDir = Path.Combine(TempRoot, "pvars");
        ExpressionEngine.GvarsPath = Path.Combine(TempRoot, "gvars.txt");
        ExpressionEngine.ItemGiverDir = Path.Combine(TempRoot, "ItemGiver");
        // The dungeon map bake writes floor plans under C:\Games\RynthSuite\RynthAi\Maps.
        RynthCore.Plugin.RynthAi.Maps.DungeonMapBake.CacheDir = Path.Combine(TempRoot, "Maps");
        // A sighted lava/acid hotspot is persisted under C:\Games\RynthSuite\RynthAi\DungeonHazards.
        LiveHazardFolder = RynthCore.Plugin.RynthAi.DungeonHazardStore.Folder;
        RynthCore.Plugin.RynthAi.DungeonHazardStore.Folder = Path.Combine(TempRoot, "DungeonHazards");
        // Detector C's flagged floor textures are persisted to C:\Games\RynthSuite\RynthAi\HazardTextures.txt.
        LiveHazardTextureFolder = RynthCore.Plugin.RynthAi.DungeonHazardSurfaces.Folder;
        RynthCore.Plugin.RynthAi.DungeonHazardSurfaces.Folder = Path.Combine(TempRoot, "HazardTextures");
        RynthCore.Plugin.RynthAi.DungeonHazardSurfaces.Reload();
        // Appraised creatures are saved to C:\Games\RynthSuite\RynthAi\CreatureData\creatures.json.
        LiveCreatureFolder = RynthCore.Plugin.RynthAi.CreatureData.CreatureProfileStore.Folder;
        RynthCore.Plugin.RynthAi.CreatureData.CreatureProfileStore.Folder = Path.Combine(TempRoot, "CreatureData");

        // Spell names (item spells, spell expiry by name) come from the plugin's embedded table.
        SpellDatabase.Load();
        // ACECustom spell catalog (Spells window, buff profiles), also embedded in the plugin.
        SpellCatalog.EnsureLoaded(Console.WriteLine);

        var runner = new Runner();
        NativeLootTests.Register(runner);
        VTankLootTests.Register(runner);
        LootRuleMatchTests.Register(runner);
        LootLateIdTests.Register(runner);
        LootWandTests.Register(runner);
        LootRaresModeTests.Register(runner);
        LootChatLineTests.Register(runner);
        LootSurveyTests.Register(runner);
        PropertyDumpTests.Register(runner);
        T11LootTests.Register(runner);
        MagItemDescriberTests.Register(runner);
        ExpressionTests.Register(runner);
        MetaTests.Register(runner);
        SelfTestMetaTests.Register(runner);
        NoKillTests.Register(runner);
        MetaSchedulerTests.Register(runner);
        NavEngineTests.Register(runner);
        NavReachTests.Register(runner);
        NavResumeTests.Register(runner);
        PatrolDoorwayTests.Register(runner);
        PatrolSettingsTests.Register(runner);
        PatrolPocketTests.Register(runner);
        PatrolPortalTests.Register(runner);
        BuffTests.Register(runner);
        SpellCatalogTests.Register(runner);
        CombatMathTests.Register(runner);
        WeaponTests.Register(runner);
        RadarSnapshotTests.Register(runner);
        AmmoTests.Register(runner);
        VTankYieldTests.Register(runner);
        OffhandTests.Register(runner);
        HandSwapTests.Register(runner);
        FloodPerfTests.Register(runner);
        LootSalvageTests.Register(runner);
        LootCorpseTests.Register(runner);
        LootRetryTimingTests.Register(runner);
        LootClaimMismatchTests.Register(runner);
        LootNothingLineTests.Register(runner);
        ChestLootTests.Register(runner);
        OwnCorpseTests.Register(runner);
        UseRefusalTests.Register(runner);
        CorpseKillerTests.Register(runner);
        VitalsTests.Register(runner);
        MultiTargetTests.Register(runner);
        CastSafetyTests.Register(runner);
        HealLatencyTests.Register(runner);
        BlastTests.Register(runner);
        ArcWhenClearTests.Register(runner);
        WallTests.Register(runner);
        CombatHookTests.Register(runner);
        UseAtLoginTests.Register(runner);
        LearnSpellsTests.Register(runner);
        AttackLatencyTests.Register(runner);
        TargetingTests.Register(runner);
        LootAddItemTests.Register(runner);
        GiveTests.Register(runner);
        UbCompatTests.Register(runner);
        MetaAuditTests.Register(runner);
        MetaSampleCompatTests.Register(runner);
        IdRefusalTests.Register(runner);
        DungeonMapBakeTests.Register(runner);
        MonsterEditorTests.Register(runner);
        InstallPathsTests.Register(runner);
        PetRefillTests.Register(runner);
        AutoStackTests.Register(runner);
        AutoVendorSplitTests.Register(runner);
        AutoVendorZeroRateTests.Register(runner);
        BackAwayTests.Register(runner);
        ServerPluginForwardTests.Register(runner);
        IgnoreMonsterTests.Register(runner);
        WorldCacheTypedTests.Register(runner);
        NavOverlayTests.Register(runner);
        ActionPacingTests.Register(runner);
        HazardEscapeTests.Register(runner);
        HazardFloorTests.Register(runner);
        ManaStoneChoiceTests.Register(runner);
        CustomDebuffTests.Register(runner);
        DebuffRenewTests.Register(runner);
        VoidDebuffTests.Register(runner);
        ProfileLoadTests.Register(runner);
        ActivityInterfaceTests.Register(runner);
        CastNoDamageTests.Register(runner);
        ManaReserveTests.Register(runner);
        DamageRowsTests.Register(runner);
        NavMapEditTests.Register(runner);
        LootSpeedTests.Register(runner);
        MeleeStallTests.Register(runner);
        ServerMenuTests.Register(runner);
        MiniRemoteMoveTests.Register(runner);
        SessionDeathTests.Register(runner);
        SectionsTests.Register(runner);
        ItemsCleanTests.Register(runner);
        DeathRecallTests.Register(runner);
        InventoryAttributeFilterTests.Register(runner);
        StuckCastTests.Register(runner);
        KillNoticeTests.Register(runner);
        PacerEdgeTests.Register(runner);
        RecoveryRetryTests.Register(runner);
        HpFailsafeTests.Register(runner);
        PluginFixTests.Register(runner);
        BuffChainTests.Register(runner);
        ShieldBuffTests.Register(runner);
        RebuffWhenIdleTests.Register(runner);
        LootPacingTests.Register(runner);
        LootJumpTests.Register(runner);
        CorpseCloseTests.Register(runner);
        VTankHotkeyTests.Register(runner);
        FightSelectedTests.Register(runner);
        OneTouchHealTests.Register(runner);
        SaveFailureTests.Register(runner);
        EnvironmentImpactTests.Register(runner);
        SkCombatAddonTests.Register(runner);
        DoorTests.Register(runner);
        ServerPolicyTests.Register(runner);
        BlankLoadTests.Register(runner);
        GiveProfileTests.Register(runner);
        TinkerTests.Register(runner);
        LauncherSwapTests.Register(runner);
        MissileCraftingTests.Register(runner);
        SelfRatingsTests.Register(runner);
        ServerHubClientLookupTests.Register(runner);
        UbMtSafetyTests.Register(runner);
        DifficultyTierTests.Register(runner);
        TickIsolationTests.Register(runner);
        ChoresInFightTests.Register(runner);
        CommandReplyTests.Register(runner);
        MonsterStatsTests.Register(runner);
        MetaSaveTests.Register(runner);
        return runner.Run("RynthAi host tests", args.Length > 0 ? args[0] : null);
    }
}
