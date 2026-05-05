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
[JsonSourceGenerationOptions(
    WriteIndented = true,
    IncludeFields = true,
    PropertyNameCaseInsensitive = true)]
internal partial class RynthAiJsonContext : JsonSerializerContext { }
