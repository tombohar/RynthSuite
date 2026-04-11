using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.MonsterEditor;

[JsonSerializable(typeof(List<MonsterRule>))]
[JsonSerializable(typeof(EditorSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, IncludeFields = true)]
internal partial class EditorJsonContext : JsonSerializerContext { }
