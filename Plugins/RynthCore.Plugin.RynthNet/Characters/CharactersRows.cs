// ============================================================================
//  RynthNet - Characters/CharactersRows.cs
//  The Characters window's rows, built on the plugin tick (twice a second while
//  the window is drawn) from RynthNet's own snapshot and this client's pose,
//  vitals and object table (through ICharacterGame), then published whole for
//  the render thread to draw. Moved from RynthCore's UI/Data/CharactersData.cs
//  (2026-10-21); the one difference is that a surface building is known from
//  the Atlas only (the engine also used every outdoor object it could see).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthNet.Characters;

/// <summary>What the rows need from the game (RynthNet's host; a fake in Tools/RynthNet.Tests). Plugin tick.</summary>
internal interface ICharacterGame
{
    uint PlayerId { get; }
    bool InWorld { get; }
    bool Portaling { get; }
    uint SelectedId { get; }
    bool TryGetPose(out uint cell, out float x, out float y, out float z);
    bool TryGetVitals(out uint hp, out uint hpMax, out uint st, out uint stMax, out uint mp, out uint mpMax);
    /// <summary>The object is in this client's table (it can be selected).</summary>
    bool TryGetName(uint id, out string name);
    bool TryGetPosition(uint id, out uint cell, out float x, out float y, out float z);
}

/// <summary>A vital bar: fill and "cur/max".</summary>
internal readonly record struct CharVital(float Fraction, string Text);

/// <summary>One row, preformatted on the tick.</summary>
internal sealed class CharacterRow
{
    public string Name = "";
    /// <summary>The server's name when it differs from this client's ("" otherwise).</summary>
    public string OtherWorld = "";
    public bool IsSelf;
    public bool LoggedOut;
    public uint Id;
    /// <summary>The character is in this client's object table (a click selects it).</summary>
    public bool InView;

    public bool HasVitals;
    public CharVital Hp, St, Mp;

    public bool Measured;
    public float Bearing = float.NaN;
    public string DistanceText = "";
    public string Compass = "";
    public string Location = "";

    public string StateText = "";
    public CharStateKind StateKind;
    public bool Stale;
    public string StaleText = "";
    public string Tooltip = "";
}

internal sealed class CharactersView
{
    public static readonly CharactersView Empty = new(Array.Empty<CharacterRow>(), "Waiting for RynthNet...", "", 0);

    public CharactersView(CharacterRow[] rows, string status, string myName, uint selectedId)
    {
        Rows = rows;
        Status = status;
        MyName = myName;
        SelectedId = selectedId;
    }

    /// <summary>This client first (when logged in), then the others in a stable order.</summary>
    public CharacterRow[] Rows { get; }
    /// <summary>The line above the rows: how many, or why there are none.</summary>
    public string Status { get; }
    public string MyName { get; }
    /// <summary>The game's selection when the rows were built (its row is highlighted).</summary>
    public uint SelectedId { get; }
}

internal static class CharactersRows
{
    private static readonly Func<uint, bool> IsOverworldBlock = AtlasNames.IsMapLandblock;
    private static readonly Func<uint, string> DungeonName = AtlasNames.DungeonName;

    /// <summary>The rows for one snapshot. Plugin tick.</summary>
    public static CharactersView Build(NetCharSnapshot snap, ICharacterGame game)
    {
        bool inWorld = game.InWorld && game.PlayerId != 0;
        uint myCell = 0;
        float mx = 0, my = 0, mz = 0;
        bool havePose = inWorld && game.TryGetPose(out myCell, out mx, out my, out mz) && (myCell >> 16) != 0;
        if (!havePose) { myCell = 0; mx = my = mz = 0; }
        bool portaling = game.Portaling;

        var sorted = new List<NetCharEntry>(snap.Clients);
        sorted.Sort(CharactersModel.CompareRows);

        var rows = new List<CharacterRow>(sorted.Count + 1);
        string myName = snap.MyName;
        if (inWorld)
        {
            if (myName.Length == 0 && game.TryGetName(game.PlayerId, out string n)) myName = n;
            rows.Add(SelfRow(game, myName, snap.MyWorld, havePose, myCell, mx, my, portaling));
        }

        int others = 0, stale = 0;
        foreach (NetCharEntry e in sorted)
        {
            CharacterRow row = OtherRow(game, e, snap.MyWorld, havePose && !portaling, myCell, mx, my, mz);
            rows.Add(row);
            if (!row.LoggedOut) others++;
            if (row.Stale) stale++;
        }

        string status;
        if (!snap.Online && snap.Version.Length > 0)
            status = "RynthNet is offline" + (snap.Offline.Length > 0 ? ": " + snap.Offline : ".");
        else if (sorted.Count == 0)
            status = "No other RynthCore clients with RynthNet on this PC.";
        else
            status = others == 1 ? "1 other character" : others + " other characters";
        if (stale > 0) status += $" ({stale} not updating)";
        return new CharactersView(rows.ToArray(), status, myName, inWorld ? game.SelectedId : 0);
    }

    private static CharacterRow SelfRow(ICharacterGame game, string name, string world, bool havePose, uint cell, float x, float y, bool portaling)
    {
        var row = new CharacterRow
        {
            Name = name.Length > 0 ? name : "You",
            IsSelf = true,
            Id = game.PlayerId,
            StateText = "this client",
        };
        if (game.TryGetVitals(out uint hp, out uint hpMax, out uint st, out uint stMax, out uint mp, out uint mpMax) && hpMax > 0)
        {
            row.HasVitals = true;
            row.Hp = Vital(hp, hpMax);
            row.St = Vital(st, stMax);
            row.Mp = Vital(mp, mpMax);
        }
        if (portaling) row.Location = "portal space";
        else if (havePose)
        {
            uint lb = cell >> 16;
            bool over = CharactersModel.IsOutdoorCell(cell) || IsOverworldBlock(lb);
            row.Location = over ? CharactersModel.Coords(cell, x, y) : AtlasNames.DungeonLabel(lb);
        }
        row.Tooltip = row.Name + " (this client)" + (world.Length > 0 ? "\n" + world : "");
        return row;
    }

    private static CharacterRow OtherRow(ICharacterGame game, NetCharEntry e, string myWorld, bool canMeasure, uint myCell, float mx, float my, float mz)
    {
        (string stateText, CharStateKind kind) = CharactersModel.State(e);
        var row = new CharacterRow
        {
            Name = e.LoggedIn && e.Name.Length > 0 ? e.Name : "Not logged in (pid " + e.Pid.ToString(CultureInfo.InvariantCulture) + ")",
            LoggedOut = kind == CharStateKind.LoggedOut,
            Id = e.Id,
            StateText = stateText,
            StateKind = kind,
            Stale = CharactersModel.IsStale(e),
        };
        if (row.Stale) row.StaleText = "no update " + (e.AgeMs / 1000).ToString(CultureInfo.InvariantCulture) + " s";
        bool sameWorld = e.World.Length == 0 || myWorld.Length == 0 || string.Equals(e.World, myWorld, StringComparison.OrdinalIgnoreCase);
        if (!sameWorld) row.OtherWorld = e.World;

        if (e.HasVitals && e.HpMax > 0)
        {
            row.HasVitals = true;
            row.Hp = Vital(e.Hp, e.HpMax);
            row.St = Vital(e.St, e.StMax);
            row.Mp = Vital(e.Mp, e.MpMax);
        }

        // Where: this client's own view of the character when it has one (fresher), else the status.
        uint cell = e.Cell;
        float x = e.X, y = e.Y, z = e.Z;
        bool hasPose = e.HasPose;
        if (sameWorld && e.Id != 0 && game.TryGetName(e.Id, out _))
        {
            row.InView = true;
            if (game.TryGetPosition(e.Id, out uint c2, out float x2, out float y2, out float z2) && (c2 >> 16) != 0)
            {
                cell = c2; x = x2; y = y2; z = z2;
                hasPose = true;
            }
        }

        if (row.LoggedOut) row.Location = "";
        else if (!sameWorld) row.Location = "on " + e.World;
        else if (e.Portal) row.Location = "portal space";
        else if (hasPose)
        {
            CharPlace p = canMeasure
                ? CharactersModel.Locate(myCell, mx, my, mz, cell, x, y, z, IsOverworldBlock, DungeonName)
                : CharactersModel.Locate(0, 0, 0, 0, cell, x, y, z, IsOverworldBlock, DungeonName);
            row.Measured = p.Measured;
            row.Bearing = p.Bearing;
            row.DistanceText = p.Measured ? p.DistanceText : "elsewhere";
            row.Compass = p.Compass;
            row.Location = p.Location;
        }
        else if (e.HasCoords)
        {
            row.Location = CharactersModel.CoordsText(e.Ns, e.Ew);
        }

        row.Tooltip = Tooltip(e, row, cell, hasPose);
        return row;
    }

    private static CharVital Vital(uint cur, uint max) => new(CharactersModel.Fraction(cur, max), CharactersModel.VitalText(cur, max));

    private static string Tooltip(NetCharEntry e, CharacterRow row, uint cell, bool hasPose)
    {
        var sb = new StringBuilder(200);
        sb.Append(row.Name).Append('\n');
        if (e.World.Length > 0) sb.Append(e.World).Append('\n');
        if (e.Tags.Length > 0) sb.Append("tags: ").Append(string.Join(", ", e.Tags)).Append('\n');
        sb.Append(row.StateText);
        if (e.HasBot && e.Action.Length > 0 && !e.Macro) sb.Append(" (last action: ").Append(e.Action).Append(')');
        sb.Append('\n');
        if (row.Measured) sb.Append(row.DistanceText).Append(' ').Append(row.Compass).Append(", ");
        else if (hasPose && row.OtherWorld.Length == 0) sb.Append("Elsewhere: not in the same space as you (another dungeon or landblock), so no distance.\n");
        if (row.Location.Length > 0) sb.Append(row.Location);
        if (hasPose) sb.Append(" (LB ").Append((cell >> 16).ToString("X4", CultureInfo.InvariantCulture)).Append(')');
        sb.Append('\n');
        if (e.AgeMs >= 0) sb.Append("status ").Append((e.AgeMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)).Append(" s old\n");
        if (row.Id == 0) sb.Append("Its RynthNet is older than 0.1.1: no object id, so a click can't select it.");
        else if (row.InView) sb.Append("Click to select. Right-click for commands.");
        else sb.Append("Not in view: a click can't select it. Right-click for commands.");
        return sb.ToString();
    }
}
