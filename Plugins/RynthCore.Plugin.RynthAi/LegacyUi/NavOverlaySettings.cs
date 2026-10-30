// NavOverlaySettings.cs — display + recording options for the Navigation window:
// route marker colours, the on-screen waypoint HUD / labels, the guide line to the
// active waypoint, the breadcrumb trail and route recording.
//
// Stored per character inside LegacyUiSettings.NavOverlay (public fields, serialized via
// RynthAiJsonContext IncludeFields). Colours are D3D9 ARGB (0xAARRGGBB).
using System;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>Persisted Navigation display / breadcrumb / recording options.</summary>
public sealed class NavOverlaySettings
{
    // ── Route markers (3D rings + lines) ─────────────────────────────────────
    /// <summary>
    /// Route overlay master switch (Nav panel "Route overlay"): the route's rings and connecting
    /// lines, the waypoint labels and the guide line. The waypoint HUD window has its own switch.
    /// </summary>
    public bool ShowRouteMarkers = true;
    /// <summary>Upper bound on route points drawn around the active waypoint.</summary>
    public int MaxRouteMarkers = 128;
    public uint ColorRing       = 0xFF00FFFF; // cyan
    public uint ColorActiveRing = 0xFFFF4444; // red
    public uint ColorLine       = 0xFF0088FF; // blue

    // ── Waypoint HUD + on-screen labels ──────────────────────────────────────
    /// <summary>Small movable window: active waypoint, distance, direction arrow, next step.</summary>
    public bool ShowHud = true;
    /// <summary>Only show the HUD while navigation is enabled (otherwise whenever a route is loaded).</summary>
    public bool HudOnlyWhileNavigating;
    /// <summary>"#12 · 34yd" text drawn over the upcoming waypoints in the 3D view.</summary>
    public bool ShowWaypointLabels = true;
    /// <summary>How many upcoming waypoints get a label (active included).</summary>
    public int LabelsAhead = 3;
    /// <summary>Line from the player to the active waypoint (UB "breadcrumb" line).</summary>
    public bool ShowGuideLine = true;
    public uint ColorGuide = 0xFFFFDC28; // yellow

    // ── Breadcrumb trail ─────────────────────────────────────────────────────
    /// <summary>
    /// Record where the character walks, independent of any route (Nav panel "Breadcrumbs").
    /// While off, the trail is neither extended nor drawn.
    /// </summary>
    public bool TrackBreadcrumbs = true;
    /// <summary>Draw the recorded trail on the ground.</summary>
    public bool ShowBreadcrumbs = true;
    /// <summary>Minimum distance between trail samples, yards.</summary>
    public float BreadcrumbSpacingYards = 3f;
    /// <summary>Trail length cap (oldest samples drop off).</summary>
    public int BreadcrumbMaxPoints = 400;
    /// <summary>Trail segments farther than this from the player are not drawn, yards.</summary>
    public float BreadcrumbDrawRangeYards = 150f;
    /// <summary>Older trail segments fade out.</summary>
    public bool BreadcrumbFade = true;
    public uint ColorBreadcrumb = 0xFFFF9A2E; // orange

    // ── Route recording ──────────────────────────────────────────────────────
    /// <summary>Distance between auto-added Point waypoints while recording, yards.</summary>
    public float RecordSpacingYards = 5f;
    /// <summary>On a teleport while recording, add a Portal step for the portal used just before it.</summary>
    public bool RecordPortals = true;
    /// <summary>Pause added after an auto-recorded portal step, ms (0 = none).</summary>
    public int RecordPauseAfterPortalMs = 1500;

    // Effective visibility (methods, not properties, so the settings JSON doesn't gain fields).

    /// <summary>Breadcrumb trail is drawn: tracking on and the trail display on.</summary>
    public bool TrailVisible() => TrackBreadcrumbs && ShowBreadcrumbs;

    /// <summary>Guide line to the active waypoint is drawn (part of the route overlay).</summary>
    public bool GuideLineVisible() => ShowRouteMarkers && ShowGuideLine;

    /// <summary>Waypoint labels are drawn (part of the route overlay).</summary>
    public bool LabelsVisible() => ShowRouteMarkers && ShowWaypointLabels;

    /// <summary>Clamp values a hand-edited or older profile could carry out of range.</summary>
    public void Sanitize()
    {
        MaxRouteMarkers = Math.Clamp(MaxRouteMarkers, 8, 128);
        LabelsAhead = Math.Clamp(LabelsAhead, 1, 10);
        BreadcrumbSpacingYards = Clamp(BreadcrumbSpacingYards, 0.5f, 50f, 3f);
        BreadcrumbMaxPoints = Math.Clamp(BreadcrumbMaxPoints, 20, 2000);
        BreadcrumbDrawRangeYards = Clamp(BreadcrumbDrawRangeYards, 20f, 500f, 150f);
        RecordSpacingYards = Clamp(RecordSpacingYards, 1f, 50f, 5f);
        RecordPauseAfterPortalMs = Math.Clamp(RecordPauseAfterPortalMs, 0, 30000);
    }

    /// <summary>Display defaults (recording / trail options keep their values).</summary>
    public void ResetColors()
    {
        var d = new NavOverlaySettings();
        ColorRing = d.ColorRing; ColorActiveRing = d.ColorActiveRing; ColorLine = d.ColorLine;
        ColorGuide = d.ColorGuide; ColorBreadcrumb = d.ColorBreadcrumb;
    }

    private static float Clamp(float v, float min, float max, float fallback)
        => float.IsNaN(v) ? fallback : Math.Clamp(v, min, max);

    /// <summary>D3D9 ARGB → ImGui ABGR (swap red and blue).</summary>
    public static uint ArgbToImGui(uint argb)
        => (argb & 0xFF00FF00) | ((argb & 0x00FF0000) >> 16) | ((argb & 0x000000FF) << 16);

    /// <summary>Same colour with its alpha scaled by <paramref name="factor"/> (0..1).</summary>
    public static uint ScaleAlpha(uint argb, float factor)
    {
        uint a = (uint)Math.Clamp((argb >> 24) * factor, 0f, 255f);
        return (a << 24) | (argb & 0x00FFFFFF);
    }
}
