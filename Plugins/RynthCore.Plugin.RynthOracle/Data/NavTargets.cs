using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// Coordinates in Oracle's text (quest walkthroughs, Dynamic Stamp notes) as RynthNav targets.
/// The lists write them every which way: "59.6S 88.0W", "8.8S, 53.1E", "42.6n 62.4e".
/// </summary>
internal static class NavTargets
{
    private static readonly Regex CoordsRx = new(
        @"(?<ns>\d+(?:\.\d+)?)\s*(?<nsc>[NnSs])[\s,]*(?<ew>\d+(?:\.\d+)?)\s*(?<ewc>[EeWw])\b",
        RegexOptions.CultureInvariant);

    /// <summary>One place to go: what the text calls it (may be empty) and its coordinates ("59.6S, 88.0W").</summary>
    internal readonly record struct Stop(string Label, string Coords)
    {
        /// <summary>The RynthNav target: the coordinates, then the label (RynthNav uses an Atlas
        /// place of that name there, e.g. a dungeon's entrance).</summary>
        public string Target => Label.Length > 0 ? Coords + " " + Label : Coords;

        /// <summary>How a picker shows it: "Kill Mukkir Progenitor - 86.5N, 57.3E".</summary>
        public string Display => Label.Length > 0 ? Label + " - " + Coords : Coords;
    }

    /// <summary>
    /// A named place with no coordinates (an exploration marker's location): its name is the whole
    /// RynthNav target, and RynthNav finds it on its Atlas.
    /// </summary>
    public static Stop Place(string name) => new("", name.Trim());

    /// <summary>The first coordinates in <paramref name="text"/>, written "8.8S, 53.1E", or null.</summary>
    public static string? FirstCoords(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        Match m = CoordsRx.Match(text);
        return m.Success ? Format(m) : null;
    }

    /// <summary><paramref name="text"/> with its coordinates taken out.</summary>
    public static string WithoutCoords(string? text) => string.IsNullOrEmpty(text) ? "" : CoordsRx.Replace(text, " ");

    /// <summary>
    /// Every coordinate pair in <paramref name="text"/>, in order, each labelled with the words just
    /// before it in its step (walkthroughs separate steps with " - "): "Speak with Hoshar ibn Jalaq
    /// 59.6S 88.0W - Kill Mukkir Progenitor 86.5N 57.3E" gives two stops. The same coordinates
    /// twice are one stop.
    /// </summary>
    public static List<Stop> Stops(string? text)
    {
        var stops = new List<Stop>();
        if (string.IsNullOrEmpty(text)) return stops;
        foreach (Match m in CoordsRx.Matches(text))
        {
            string coords = Format(m);
            if (stops.Exists(s => s.Coords == coords)) continue;
            stops.Add(new Stop(LabelBefore(text, m.Index), coords));
        }
        return stops;
    }

    /// <summary>The words between the previous step break (" - ", ". ", "; ", "|") or coordinates and <paramref name="at"/>.</summary>
    private static string LabelBefore(string text, int at)
    {
        int from = 0;
        foreach (string sep in new[] { " - ", ". ", "; ", "|", "(" })
        {
            int i = text.LastIndexOf(sep, Math.Max(0, at - 1), StringComparison.Ordinal);
            if (i >= 0 && i + sep.Length <= at) from = Math.Max(from, i + sep.Length);
        }
        // Not back past the previous coordinates ("Cragstone 26.0N 47.0E Speak with ...").
        foreach (Match m in CoordsRx.Matches(text.Substring(0, at)))
            from = Math.Max(from, m.Index + m.Length);
        string label = text.Substring(from, at - from).Trim(' ', ',', ':', '-', '.', '(', ')');
        // "at 42.1N ..." and "Meet at 42.1N ...": the little words around the coordinates aren't the place.
        label = Regex.Replace(label, @"^(at|in|near|to)\s+|\s+(at|in|near|to)$", "", RegexOptions.IgnoreCase).Trim();
        return label.Length > 60 ? label.Substring(0, 60).TrimEnd() : label;
    }

    private static string Format(Match m) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{m.Groups["ns"].Value}{char.ToUpperInvariant(m.Groups["nsc"].Value[0])}, {m.Groups["ew"].Value}{char.ToUpperInvariant(m.Groups["ewc"].Value[0])}");
}
