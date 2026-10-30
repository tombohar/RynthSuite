using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RynthCore.Plugin.RynthNav;

// Where a character's personal recalls land. The client is never told (the server keeps
// a character's lifestone, ties, last portal and house), so RynthNav learns them from what
// it sees: the server's messages ("You have attuned your spirit to this Lifestone.", "X is
// recalling home.") and the teleport that follows, and a teleport right after your own spell
// words (a recall cast by hand: RecallPlanner.GuessHandCast). ACE sends a character neither
// its tie positions (PlayerDescription carries only LastOutsideDeath) nor the tied portals'
// data ids (LinkedPortalOne/Two aren't SendOnLogin and change silently), so a tie made
// before RynthNav was there is learned on the first cast of its recall. Saved per character in
// NavData\recalls.txt. No host calls: the offline tests compile this file.
// (GoArrow kept the same kind of per-character recall list; Digero 2006, Virindi 2011, MIT.)

internal enum RecallSlot
{
    /// <summary>Where @lifestone (the recall everyone has) lands: the last lifestone you used.</summary>
    Sanctuary,
    /// <summary>Where the Lifestone Recall spell lands: the lifestone you cast Lifestone Tie on.</summary>
    Lifestone,
    /// <summary>Where Portal Recall lands: the last portal you walked through.</summary>
    LastPortal,
    /// <summary>Primary Portal Tie Recall.</summary>
    Tie1,
    /// <summary>Secondary Portal Tie Recall.</summary>
    Tie2,
    /// <summary>House recall.</summary>
    House,
    /// <summary>Allegiance mansion (housing) recall.</summary>
    Mansion,
    /// <summary>Allegiance hometown recall.</summary>
    Hometown,
}

internal sealed class RecallSpot
{
    public bool OnMap;
    public double Ns, Ew;
    public string Label = "";
    /// <summary>The chat command that does this recall, when it's a command (@lifestone, @house recall).</summary>
    public string Command = "";
}

/// <summary>What a chat line tells RynthNav about recalls.</summary>
internal enum RecallEvent
{
    None,
    /// <summary>"You have attuned your spirit to this Lifestone." (used a lifestone: @lifestone lands here).</summary>
    AttunedLifestone,
    /// <summary>"You have successfully linked with the life stone." (Lifestone Tie).</summary>
    LinkedLifestone,
    /// <summary>"You have successfully linked with the portal." (a Portal Tie; which one isn't said).</summary>
    LinkedPortal,
    /// <summary>"Name is recalling to the lifestone." (@lifestone under way).</summary>
    RecallingLifestone,
    /// <summary>"Name is recalling home."</summary>
    RecallingHome,
    /// <summary>"Name is recalling to the Allegiance housing."</summary>
    RecallingMansion,
    /// <summary>"Name is going to the Allegiance hometown."</summary>
    RecallingHometown,
    /// <summary>"Name is recalling to the marketplace." (lands in a dungeon: never used for routes).</summary>
    RecallingMarketplace,
    /// <summary>"You say, "Zojak Ququ"" on the spellcasting channel: you started casting a spell
    /// (a recall cast by hand teleports a few seconds later).</summary>
    OwnSpellWords,
}

internal sealed class RecallMemory
{
    private readonly Dictionary<string, Dictionary<RecallSlot, RecallSpot>> _byChar = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bumped on every change (the plugin saves when it moves).</summary>
    public int Version { get; private set; }

    public RecallSpot? Get(string charKey, RecallSlot slot) =>
        _byChar.TryGetValue(charKey, out var d) && d.TryGetValue(slot, out var s) ? s : null;

    public void Set(string charKey, RecallSlot slot, RecallSpot spot)
    {
        if (!_byChar.TryGetValue(charKey, out var d)) _byChar[charKey] = d = new Dictionary<RecallSlot, RecallSpot>();
        d[slot] = spot;
        Version++;
    }

    public void Clear(string charKey, RecallSlot slot)
    {
        if (_byChar.TryGetValue(charKey, out var d) && d.Remove(slot)) Version++;
    }

    /// <summary>The command for a command recall, as the retail client's own help spells it.</summary>
    public static string DefaultCommand(RecallSlot slot) => slot switch
    {
        RecallSlot.Sanctuary => "@lifestone",
        RecallSlot.House => "@house recall",
        RecallSlot.Mansion => "@house mansion_recall",
        RecallSlot.Hometown => "@allegiance hometown",
        _ => "",
    };

    /// <summary>The recall a "... is recalling ..." line announces, if any.</summary>
    public static bool SlotFor(RecallEvent e, out RecallSlot slot)
    {
        switch (e)
        {
            case RecallEvent.RecallingLifestone: slot = RecallSlot.Sanctuary; return true;
            case RecallEvent.RecallingHome: slot = RecallSlot.House; return true;
            case RecallEvent.RecallingMansion: slot = RecallSlot.Mansion; return true;
            case RecallEvent.RecallingHometown: slot = RecallSlot.Hometown; return true;
            default: slot = RecallSlot.Sanctuary; return false;
        }
    }

    /// <summary>ChatMessageType.Spellcasting: the words a caster speaks (yours: "You say, "...").</summary>
    public const int ChatTypeSpellcasting = 17;

    /// <summary>
    /// Reads a chat line (the server's own wording, ACE's Player_Location / Lifestone /
    /// WorldObject_Magic). <paramref name="playerName"/> picks out your own "is recalling"
    /// lines from other players' nearby. <paramref name="chatType"/> tells your spell words
    /// (Spellcasting, 17) from something you typed in local chat (Speech, 2).
    /// </summary>
    public static RecallEvent Classify(string? text, string? playerName, int chatType = -1)
    {
        if (string.IsNullOrEmpty(text)) return RecallEvent.None;
        string t = text.Trim();
        if (chatType == ChatTypeSpellcasting && t.StartsWith("You say, \"", StringComparison.Ordinal))
            return RecallEvent.OwnSpellWords;
        if (t.StartsWith("You have attuned your spirit to this Lifestone", StringComparison.OrdinalIgnoreCase))
            return RecallEvent.AttunedLifestone;
        if (t.StartsWith("You have successfully linked with the life stone", StringComparison.OrdinalIgnoreCase))
            return RecallEvent.LinkedLifestone;
        if (t.StartsWith("You have successfully linked with the portal", StringComparison.OrdinalIgnoreCase))
            return RecallEvent.LinkedPortal;
        if (string.IsNullOrEmpty(playerName) || !t.StartsWith(playerName + " ", StringComparison.Ordinal))
            return RecallEvent.None;
        string rest = t.Substring(playerName.Length + 1);
        if (rest.StartsWith("is recalling to the lifestone", StringComparison.OrdinalIgnoreCase)) return RecallEvent.RecallingLifestone;
        if (rest.StartsWith("is recalling home", StringComparison.OrdinalIgnoreCase)) return RecallEvent.RecallingHome;
        if (rest.StartsWith("is recalling to the Allegiance housing", StringComparison.OrdinalIgnoreCase)) return RecallEvent.RecallingMansion;
        if (rest.StartsWith("is going to the Allegiance hometown", StringComparison.OrdinalIgnoreCase)) return RecallEvent.RecallingHometown;
        if (rest.StartsWith("is recalling to the marketplace", StringComparison.OrdinalIgnoreCase)) return RecallEvent.RecallingMarketplace;
        return RecallEvent.None;
    }

    // ── recalls.txt ──────────────────────────────────────────────────────────
    // # comment
    // <character>\t<slot>\t<ns or ->\t<ew or ->\t<label>\t<command>

    public string Serialize()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("# RynthNav: where each character's recalls land, learned in game. One line per recall:\r\n");
        sb.Append("# character<TAB>recall<TAB>NS<TAB>EW<TAB>label<TAB>command (- for a place off the map)\r\n");
        var chars = new List<string>(_byChar.Keys);
        chars.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string c in chars)
        {
            var slots = new List<RecallSlot>(_byChar[c].Keys);
            slots.Sort();
            foreach (RecallSlot slot in slots)
            {
                RecallSpot s = _byChar[c][slot];
                sb.Append(Clean(c)).Append('\t').Append(SlotName(slot)).Append('\t')
                  .Append(s.OnMap ? s.Ns.ToString("F2", ci) : "-").Append('\t')
                  .Append(s.OnMap ? s.Ew.ToString("F2", ci) : "-").Append('\t')
                  .Append(Clean(s.Label)).Append('\t').Append(Clean(s.Command)).Append("\r\n");
            }
        }
        return sb.ToString();
    }

    public static RecallMemory Parse(string text)
    {
        var m = new RecallMemory();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split('\t');
            if (f.Length < 4 || f[0].Length == 0) continue;
            if (!TrySlot(f[1], out RecallSlot slot)) continue;
            var s = new RecallSpot
            {
                Label = f.Length > 4 ? f[4] : "",
                Command = f.Length > 5 ? f[5] : "",
            };
            s.OnMap = double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s.Ns)
                    & double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out s.Ew);
            m.Set(f[0], slot, s);
        }
        m.Version = 0;
        return m;
    }

    // Slot names by hand, not Enum.ToString / Enum.TryParse (NativeAOT plugin; no reflection).
    private static readonly string[] SlotNames = { "Sanctuary", "Lifestone", "LastPortal", "Tie1", "Tie2", "House", "Mansion", "Hometown" };

    public static string SlotName(RecallSlot slot) => (int)slot >= 0 && (int)slot < SlotNames.Length ? SlotNames[(int)slot] : "?";

    public static bool TrySlot(string name, out RecallSlot slot)
    {
        for (int i = 0; i < SlotNames.Length; i++)
            if (SlotNames[i].Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)) { slot = (RecallSlot)i; return true; }
        slot = RecallSlot.Sanctuary;
        return false;
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
