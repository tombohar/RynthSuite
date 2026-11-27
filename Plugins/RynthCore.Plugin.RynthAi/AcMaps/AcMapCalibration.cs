// ============================================================================
//  RynthAi - AcMaps/AcMapCalibration.cs
//  Where you are on an ACMAPS map: the dungeon position (the /loc x, y, z in
//  the landblock) to a pixel on the image, set by the player once per map.
//
//  ACMAPS maps carry no coordinates. GoArrow had the player drag the map until
//  its arrow sat on the entrance, then moved the map 3 pixels per unit walked
//  (DungeonHud.cs: dX = (old - new) * 3, north up). Here the player does the
//  same once and it is saved: stand somewhere you can find on the map, press
//  "Place me here" and click that spot. One spot per floor places the floor
//  with the map's scale (GoArrow's 3 pixels per unit until it is measured);
//  a second spot on a floor, some way from the first, measures the scale.
//  ACMAPS draws each floor in its own part of the image, so every floor
//  (spots within FloorTolerance of each other's height) gets its own spots.
//  The maps are north up: x east is right, y north is up (the image's -y).
//
//  Pure: tests drive it.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RynthCore.Plugin.RynthAi.AcMaps;

/// <summary>A spot the player set: where they stood (landblock x, y, z) and the pixel on the map they clicked.</summary>
internal readonly record struct MapSpot(float X, float Y, float Z, float Px, float Py);

/// <summary>What a Place did, for the window's status line.</summary>
internal enum PlaceResult
{
    /// <summary>The first spot on this floor: placed with the map's scale.</summary>
    FloorPlaced,
    /// <summary>A spot near the floor's only one: it moved there.</summary>
    Moved,
    /// <summary>The second spot on the floor: the scale was measured.</summary>
    ScaleMeasured,
    /// <summary>The two spots disagree with a north-up map (the map would be mirrored or the scale absurd): kept as the floor's only spot.</summary>
    ScaleRefused,
}

internal sealed class AcMapCalibration
{
    /// <summary>GoArrow's scale: map pixels per landblock unit walked.</summary>
    public const float DefaultScale = 3f;
    public const float MinScale = 0.2f, MaxScale = 50f;
    /// <summary>Spots this close in height are one floor (AC dungeon floors are about 6 units apart).</summary>
    public const float FloorTolerance = 3f;
    /// <summary>Two spots must be this far apart (landblock units) to measure the scale.</summary>
    public const float MinSpotDistance = 8f;
    /// <summary>The most spots a map keeps (two a floor; a dungeon has a handful of floors).</summary>
    public const int MaxSpots = 64;

    private readonly List<MapSpot> _spots = new();

    /// <summary>Map pixels per landblock unit, shared by every floor.</summary>
    public float Scale { get; private set; } = DefaultScale;
    /// <summary>The scale was measured from two spots (or set by hand), not GoArrow's default.</summary>
    public bool ScaleMeasuredOrSet { get; private set; }

    public IReadOnlyList<MapSpot> Spots => _spots;
    public bool IsEmpty => _spots.Count == 0;

    public AcMapCalibration() { }

    /// <summary>A saved calibration (AcMapSettings); bad values fall back to the defaults.</summary>
    public AcMapCalibration(float scale, bool scaleSet, IEnumerable<MapSpot> spots)
    {
        if (float.IsFinite(scale) && scale >= MinScale && scale <= MaxScale) { Scale = scale; ScaleMeasuredOrSet = scaleSet; }
        foreach (MapSpot s in spots)
        {
            if (_spots.Count >= MaxSpots) break;
            if (float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Z) && float.IsFinite(s.Px) && float.IsFinite(s.Py))
                _spots.Add(s);
        }
    }

    /// <summary>The spots on the floor at height <paramref name="z"/>, nearest in height first.</summary>
    public List<MapSpot> FloorSpots(float z)
    {
        var list = new List<MapSpot>();
        foreach (MapSpot s in _spots)
            if (MathF.Abs(s.Z - z) <= FloorTolerance) list.Add(s);
        list.Sort((a, b) => MathF.Abs(a.Z - z).CompareTo(MathF.Abs(b.Z - z)));
        return list;
    }

    /// <summary>This floor has a spot: a position on it has a pixel.</summary>
    public bool FloorPlaced(float z) => FloorSpots(z).Count > 0;

    /// <summary>
    /// The pixel for a landblock position, through the floor's spots (one: that spot and the map's scale; two:
    /// their mean). False when the floor has no spot.
    /// </summary>
    public bool TryToImage(float x, float y, float z, out Vector2 pixel)
    {
        pixel = default;
        if (!TryFloorOrigin(z, out Vector2 origin)) return false;
        pixel = new Vector2(origin.X + Scale * x, origin.Y - Scale * y);
        return true;
    }

    /// <summary>The landblock x, y for a pixel on the floor at <paramref name="z"/> (the inverse of TryToImage).</summary>
    public bool TryToWorld(Vector2 pixel, float z, out float x, out float y)
    {
        x = y = 0;
        if (!TryFloorOrigin(z, out Vector2 origin)) return false;
        x = (pixel.X - origin.X) / Scale;
        y = -(pixel.Y - origin.Y) / Scale;
        return true;
    }

    /// <summary>Where landblock (0, 0) falls on the image for this floor: each spot's pixel less its position, averaged.</summary>
    private bool TryFloorOrigin(float z, out Vector2 origin)
    {
        origin = default;
        List<MapSpot> floor = FloorSpots(z);
        if (floor.Count == 0) return false;
        int n = Math.Min(floor.Count, 2);
        for (int i = 0; i < n; i++)
            origin += new Vector2(floor[i].Px - Scale * floor[i].X, floor[i].Py + Scale * floor[i].Y);
        origin /= n;
        return true;
    }

    /// <summary>
    /// "Place me here": the player stands at (x, y, z) and clicked pixel (px, py). The floor's first spot places it;
    /// a second one at least <see cref="MinSpotDistance"/> away measures the map's scale; one closer than that
    /// moves the nearer spot. A floor keeps two spots: a third replaces the nearer.
    /// </summary>
    public PlaceResult Place(float x, float y, float z, float px, float py)
    {
        var spot = new MapSpot(x, y, z, px, py);
        List<MapSpot> floor = FloorSpots(z);
        if (floor.Count == 0)
        {
            if (_spots.Count >= MaxSpots) _spots.RemoveAt(0);
            _spots.Add(spot);
            return PlaceResult.FloorPlaced;
        }
        // The spot nearest the new one (in the dungeon) is the one replaced or kept beside it.
        MapSpot nearer = floor[0];
        float nearD = Dist(floor[0], spot);
        for (int i = 1; i < floor.Count; i++)
        {
            float d = Dist(floor[i], spot);
            if (d < nearD) { nearD = d; nearer = floor[i]; }
        }
        if (nearD < MinSpotDistance)
        {
            // Too near to measure anything: it moves (a better click on the same place).
            Replace(nearer, spot);
            return PlaceResult.Moved;
        }
        // The other spot on this floor (the one kept): with the new one they measure the scale.
        MapSpot other = nearer;
        if (floor.Count >= 2)
        {
            // Two already: the nearer one goes, the farther one stays with the new.
            MapSpot farther = floor[0].Equals(nearer) ? floor[1] : floor[0];
            _spots.Remove(nearer);
            for (int i = 2; i < floor.Count; i++) _spots.Remove(floor[i]);
            other = farther;
        }
        else
        {
            for (int i = 1; i < floor.Count; i++) _spots.Remove(floor[i]);
        }
        if (!TrySolveScale(other, spot, out float scale))
        {
            // A mirrored or absurd answer: the click was off. The new spot alone places the floor.
            _spots.Remove(other);
            _spots.Add(spot);
            return PlaceResult.ScaleRefused;
        }
        _spots.Add(spot);
        Scale = scale;
        ScaleMeasuredOrSet = true;
        return PlaceResult.ScaleMeasured;
    }

    /// <summary>
    /// The scale two spots give on a north-up map: the least-squares fit of pixel steps to (dx, -dy) steps.
    /// False when the spots are too close, or the fit is mirrored, out of range, or far from a plain scale
    /// (the pixel steps and the walked steps point more than 30 degrees apart).
    /// </summary>
    internal static bool TrySolveScale(MapSpot a, MapSpot b, out float scale)
    {
        scale = 0;
        float wx = b.X - a.X, wy = -(b.Y - a.Y);
        float ix = b.Px - a.Px, iy = b.Py - a.Py;
        float w2 = wx * wx + wy * wy;
        if (w2 < MinSpotDistance * MinSpotDistance) return false;
        float s = (ix * wx + iy * wy) / w2;
        if (!float.IsFinite(s) || s < MinScale || s > MaxScale) return false;
        float i2 = ix * ix + iy * iy;
        if (i2 <= 0) return false;
        float cos = (ix * wx + iy * wy) / MathF.Sqrt(i2 * w2);
        if (cos < MathF.Cos(30f * MathF.PI / 180f)) return false;
        scale = s;
        return true;
    }

    /// <summary>The scale nudged by hand (the window's - and +), kept in range.</summary>
    public void NudgeScale(float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0) return;
        Scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        ScaleMeasuredOrSet = true;
    }

    /// <summary>The floor at <paramref name="z"/> forgets its spots. True when it had some.</summary>
    public bool ClearFloor(float z) => _spots.RemoveAll(s => MathF.Abs(s.Z - z) <= FloorTolerance) > 0;

    /// <summary>Every spot and the scale back to the start.</summary>
    public void ClearAll()
    {
        _spots.Clear();
        Scale = DefaultScale;
        ScaleMeasuredOrSet = false;
    }

    /// <summary>How many floors have a spot (spots grouped by height).</summary>
    public int FloorCount()
    {
        var zs = new List<float>();
        foreach (MapSpot s in _spots)
            if (!zs.Exists(z => MathF.Abs(z - s.Z) <= FloorTolerance)) zs.Add(s.Z);
        return zs.Count;
    }

    private void Replace(MapSpot old, MapSpot now)
    {
        int i = _spots.IndexOf(old);
        if (i >= 0) _spots[i] = now;
        else _spots.Add(now);
    }

    private static float Dist(MapSpot a, MapSpot b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
