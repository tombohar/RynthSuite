using System.Text.Json.Serialization;
using RynthCore.Plugin.RynthAi.LegacyUi;

namespace RynthCore.Plugin.RynthAi;

[JsonSerializable(typeof(LegacyUiSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
internal partial class RynthAiJsonContext : JsonSerializerContext { }
