using System.Collections.Generic;
using System.Text.Json.Serialization;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

[JsonSerializable(typeof(LegacyUiSettings))]
[JsonSerializable(typeof(List<MonsterRule>), TypeInfoPropertyName = "MonsterRuleList")]
[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
internal partial class RynthAiJsonContext : JsonSerializerContext { }
