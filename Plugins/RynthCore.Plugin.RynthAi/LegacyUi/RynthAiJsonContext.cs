using System.Collections.Generic;
using System.Text.Json.Serialization;
using RynthCore.Plugin.RynthAi.CreatureData;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

[JsonSerializable(typeof(LegacyUiSettings))]
[JsonSerializable(typeof(List<MonsterRule>), TypeInfoPropertyName = "MonsterRuleList")]
[JsonSerializable(typeof(List<ConsumableRule>), TypeInfoPropertyName = "ConsumableRuleList")]
[JsonSerializable(typeof(Dictionary<string, CreatureProfile>), TypeInfoPropertyName = "CreatureProfileDict")]
[JsonSerializable(typeof(MonstersBridgePayload))]
[JsonSerializable(typeof(SettingsBridgePayload))]
[JsonSerializable(typeof(NavBridgePayload))]
[JsonSerializable(typeof(ItemsBridgePayload))]
[JsonSerializable(typeof(NavCommand))]
[JsonSerializable(typeof(MetaRuleDto))]
[JsonSerializable(typeof(MetaCommand))]
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.Meta.MetaScheduleConfig))]
// UB seed, translation, diagnostics, Mini Remote, HUD and item info files persist through this
// context (NativeAOT cannot reflect).
[JsonSerializable(typeof(RynthCore.CreatureSeed.UbMobSeedFile))]
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.Translate.TranslateSettings))]
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.RynthLogConfig))]
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.Huds.HudState))]
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.MiniRemote.MiniRemoteState))]
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.MiniRemote.LegacyHubFile))]
[JsonSerializable(typeof(RynthCore.Plugin.RynthAi.BuffProfile))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    IncludeFields = true,
    PropertyNameCaseInsensitive = true)]
internal partial class RynthAiJsonContext : JsonSerializerContext { }
