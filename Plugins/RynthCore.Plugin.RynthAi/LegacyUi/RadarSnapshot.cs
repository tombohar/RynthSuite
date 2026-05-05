using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

// DTOs polled by the engine-side Avalonia radar panel. Geometry (walls/fills)
// is only emitted when the engine's cached MapVersion (= landblock id) differs
// from the live one, so the steady-state payload is small.
//
// Coordinate convention: world-space landblock coords (px + (lb>>8 & 0xFF)*192,
// py + (lb & 0xFF)*192). The engine projects to screen using player pose +
// zoom + heading.

internal sealed class RadarSnapshotPayload
{
    [JsonPropertyName("mapVersion")]       public uint MapVersion       { get; set; }
    [JsonPropertyName("geometryIncluded")] public bool GeometryIncluded { get; set; }
    [JsonPropertyName("isIndoor")]         public bool IsIndoor         { get; set; }
    [JsonPropertyName("player")]           public RadarPlayer Player    { get; set; } = new();
    // AC NS/EW (positive = N/E). Only meaningful outdoors; indoor renders the
    // landblock id instead. Set to NaN when the host can't resolve them.
    [JsonPropertyName("ns")]               public double Ns             { get; set; } = double.NaN;
    [JsonPropertyName("ew")]               public double Ew             { get; set; } = double.NaN;
    [JsonPropertyName("layerZs")]          public List<float> LayerZs   { get; set; } = new();
    [JsonPropertyName("currentLayerZ")]    public float CurrentLayerZ   { get; set; }

    // Per-layer wall segments. Flattened: ax, ay, bx, by, gx, gy × N (6 floats/seg).
    [JsonPropertyName("walls")]   public List<RadarWallLayer> Walls    { get; set; } = new();

    // Fallback floor strips when no baked texture exists. x0, y0, x1, y1, type × N.
    [JsonPropertyName("fills")]   public List<RadarFillLayer> Fills    { get; set; } = new();

    // Per-layer pre-merged visited floor strips. x0, y0, x1, y1 × N.
    [JsonPropertyName("visited")] public List<RadarVisitedLayer> Visited { get; set; } = new();

    // All marker kinds included; engine filters by user setting.
    [JsonPropertyName("markers")] public List<RadarMarker> Markers { get; set; } = new();
}

internal sealed class RadarPlayer
{
    [JsonPropertyName("cellId")]    public uint  CellId    { get; set; }
    [JsonPropertyName("landblock")] public uint  Landblock { get; set; }
    [JsonPropertyName("x")]         public float X         { get; set; } // landblock-local
    [JsonPropertyName("y")]         public float Y         { get; set; }
    [JsonPropertyName("z")]         public float Z         { get; set; }
    [JsonPropertyName("worldX")]    public float WorldX    { get; set; } // global (px + lb*192)
    [JsonPropertyName("worldY")]    public float WorldY    { get; set; }
    [JsonPropertyName("heading")]   public float Heading   { get; set; } // degrees, AC convention
}

internal sealed class RadarWallLayer
{
    [JsonPropertyName("z")]        public float   Z        { get; set; }
    [JsonPropertyName("segments")] public float[] Segments { get; set; } = Array.Empty<float>();
}

internal sealed class RadarFillLayer
{
    [JsonPropertyName("z")]      public float   Z      { get; set; }
    [JsonPropertyName("strips")] public float[] Strips { get; set; } = Array.Empty<float>();
}

internal sealed class RadarVisitedLayer
{
    [JsonPropertyName("z")]      public float   Z      { get; set; }
    [JsonPropertyName("strips")] public float[] Strips { get; set; } = Array.Empty<float>();
}

// Kind: 0 = monster, 1 = npc, 2 = portal, 3 = door
internal sealed class RadarMarker
{
    [JsonPropertyName("kind")]  public byte    Kind  { get; set; }
    [JsonPropertyName("x")]     public float   X     { get; set; } // world-space
    [JsonPropertyName("y")]     public float   Y     { get; set; }
    [JsonPropertyName("z")]     public float   Z     { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
}

[JsonSerializable(typeof(RadarSnapshotPayload))]
[JsonSourceGenerationOptions(WriteIndented = false)]
internal partial class RadarSnapshotJsonContext : JsonSerializerContext { }
