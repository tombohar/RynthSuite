using System;
using System.Collections.Generic;
using RynthNav.Routing;

namespace RynthCore.Plugin.RynthNav;

internal enum ArrowStepKind { Place, Portal, Recall }

/// <summary>One thing the arrow points at: a place, a portal to walk into, or a recall to cast.</summary>
internal sealed class ArrowStep
{
    public ArrowStepKind Kind;
    public string Name = "", Type = "";
    public bool OnMap;
    public double Ns, Ew;
    /// <summary>What to do there ("walk into the portal", "cast Lyceum Recall").</summary>
    public string Hint = "";
}

internal enum ArrowEvent { None, Advanced, Arrived }

/// <summary>
/// The arrow's target, or a chain of them (a route: portals and recalls, then the place).
/// A place is reached by walking within ArriveUnits of it; a portal or recall step is done
/// when you teleport. Reaching the last step clears the arrow. (GoArrow's arrow, chained
/// along a route; Digero 2006, Virindi 2011, MIT.) Tick thread only; no host calls, so the
/// offline tests compile this file.
/// </summary>
internal sealed class ArrowGuide
{
    public const double DefaultArriveUnits = 10.0;

    private readonly List<ArrowStep> _steps = new();
    private int _index;

    public double ArriveUnits = DefaultArriveUnits;
    /// <summary>Bumped on every change (the plugin rebuilds its status text when it moves).</summary>
    public int Version { get; private set; }

    public bool Active => _index < _steps.Count;
    public ArrowStep? Current => Active ? _steps[_index] : null;
    public ArrowStep? Final => _steps.Count > 0 ? _steps[^1] : null;
    public int Index => _index;
    public int Count => _steps.Count;
    public IReadOnlyList<ArrowStep> Steps => _steps;

    public void Clear()
    {
        if (_steps.Count == 0 && _index == 0) return;
        _steps.Clear();
        _index = 0;
        Version++;
    }

    public void SetTarget(string name, string type, double ns, double ew, bool onMap = true)
    {
        _steps.Clear();
        _steps.Add(new ArrowStep { Kind = ArrowStepKind.Place, Name = name, Type = type, Ns = ns, Ew = ew, OnMap = onMap });
        _index = 0;
        Version++;
    }

    /// <summary>
    /// Points along a planned route: each portal entrance, each recall, then the place.
    /// A route with no portal or recall is just the place.
    /// </summary>
    public void SetRoute(IReadOnlyList<RouteStep> route, string name, string type, double ns, double ew)
    {
        _steps.Clear();
        foreach (RouteStep s in route)
        {
            if (s.UseRecall)
                _steps.Add(new ArrowStep { Kind = ArrowStepKind.Recall, Name = s.Label, Hint = "recall: " + s.Label });
            else if (s.IsHub)
                // Inside the Town Network: no map position; the teleport out moves the arrow on.
                _steps.Add(new ArrowStep { Kind = ArrowStepKind.Portal, Name = s.Label, Type = "Portal", Hint = "in the Town Network: walk into the " + s.Label });
            else if (s.UsePortal)
                _steps.Add(new ArrowStep
                {
                    Kind = ArrowStepKind.Portal, Name = s.Label, Type = "Portal", Ns = s.Ns, Ew = s.Ew, OnMap = true,
                    Hint = "walk into the portal",
                });
        }
        _steps.Add(new ArrowStep { Kind = ArrowStepKind.Place, Name = name, Type = type, Ns = ns, Ew = ew, OnMap = true });
        _index = 0;
        Version++;
    }

    /// <summary>
    /// Once per tick with where you are. <paramref name="teleported"/>: you went through a
    /// portal or recalled since the last call.
    /// </summary>
    public ArrowEvent Update(bool hasPos, double ns, double ew, bool teleported)
    {
        ArrowStep? cur = Current;
        if (cur == null) return ArrowEvent.None;
        bool done = cur.Kind == ArrowStepKind.Place
            ? hasPos && cur.OnMap && NavCoords.Distance(ns, ew, cur.Ns, cur.Ew) <= ArriveUnits
            : teleported;
        if (!done) return ArrowEvent.None;
        _index++;
        Version++;
        if (Active) return ArrowEvent.Advanced;
        _steps.Clear();
        _index = 0;
        return ArrowEvent.Arrived;
    }
}
