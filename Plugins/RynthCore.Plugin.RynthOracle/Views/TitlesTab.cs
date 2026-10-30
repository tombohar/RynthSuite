// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.RynthOracle.Ui;

namespace RynthCore.Plugin.RynthOracle.Views;

/// <summary>
/// Titles: every title, which ones you hold (engine API v75, from the server's title events) and
/// how to earn the rest; searchable, with held / not held filters.
/// </summary>
internal sealed partial class OracleView
{
    private const int TitlePageSize = 40;
    private string _titleSearch = "";
    private bool _titleUnavailable, _titleHeld, _titleNotHeld;
    private int _titlePage;
    private readonly List<Title> _titleRows = new();
    private string _titleKey = "\0";

    private void DrawTitles(UiWindow w)
    {
        if (_c.TitlesKnown)
        {
            int held = 0;
            foreach (Title t in TitleList.All) if (_c.EarnedTitles.Contains(t.TitleId)) held++;
            string current = "";
            foreach (Title t in TitleList.All) if (t.TitleId == (int)_c.CurrentTitle) current = t.Name;
            w.Text($"You hold {_c.EarnedTitles.Count} titles ({held} of the {TitleList.All.Count} listed).");
            if (current.Length > 0) { w.SameLine(); w.TextDisabled($"Showing: {current}"); }
        }
        else if (_c.Host.HasGetCharacterTitles)
            w.TextWrapped("The server sends your titles when you log in; this engine hasn't seen them yet for this character. Log out and back in to see which titles you hold.");
        else
            w.TextWrapped("Which titles you hold needs RynthCore's engine update (plugin API v75). Below is every title and how to earn it.");

        w.InputText("Search", "t.search", _titleSearch, 64, s => { _titleSearch = s; _titlePage = 0; });
        if (_c.TitlesKnown)
        {
            w.Checkbox("Held", "t.held", _titleHeld, v => { _titleHeld = v; _titlePage = 0; });
            w.SameLine();
            w.Checkbox("Not held", "t.notheld", _titleNotHeld, v => { _titleNotHeld = v; _titlePage = 0; });
            w.SameLine();
        }
        w.Checkbox("Include titles no longer available", "t.unavail", _titleUnavailable, v => { _titleUnavailable = v; _titlePage = 0; });

        List<Title> rows = TitleRows();
        w.Text($"{rows.Count} titles");
        w.SameLine(160f);
        _titlePage = Pager(w, "t.page", _titlePage, rows.Count, TitlePageSize, p => _titlePage = p);

        w.BeginChild("t.list", 0f, 0f, border: true);
        float x = _c.TitlesKnown ? 50f : 0f;
        if (_c.TitlesKnown) { w.TextDisabled("Held"); w.SameLine(x); }
        w.TextDisabled("Title");
        w.SameLine(x + 230f);
        w.TextDisabled("Where");
        w.SameLine(x + 470f);
        w.TextDisabled("Level");
        int start = _titlePage * TitlePageSize;
        for (int i = start; i < rows.Count && i < start + TitlePageSize; i++)
        {
            Title t = rows[i];
            Title row = t;
            bool held = _c.EarnedTitles.Contains(t.TitleId);
            if (_c.TitlesKnown) { DoneMark(w, held); w.SameLine(x); }
            w.Selectable(Cut(t.Name, 30), "t.r." + t.Number, false,
                () => PrintEntry(row.Name, "", _c.TitlesKnown ? (held ? "held" : "not held") : "", row.Hint, row.Url), 220f);
            w.SameLine(x + 230f);
            w.TextDisabled(Cut(t.Group, 36));
            w.SameLine(x + 470f);
            w.Text(t.Level > 0 ? t.Level.ToString() : "");
        }
        if (rows.Count == 0) w.TextDisabled("No titles match.");
        w.EndChild();
    }

    private List<Title> TitleRows()
    {
        bool filterHeld = _c.TitlesKnown && _titleHeld != _titleNotHeld;
        string key = $"{_titleSearch}|{_titleUnavailable}|{(filterHeld ? (_titleHeld ? "h" : "n") : "")}|{_c.EarnedTitles.Count}";
        if (key == _titleKey) return _titleRows;
        _titleKey = key;
        string[] terms = _titleSearch.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _titleRows.Clear();
        foreach (Title t in TitleList.All)
        {
            bool held = _c.EarnedTitles.Contains(t.TitleId);
            if (t.Unavailable && !_titleUnavailable && !held) continue;
            if (filterHeld && held != _titleHeld) continue;
            if (terms.Length > 0)
            {
                string hay = t.Name + " " + t.Group;
                bool all = true;
                foreach (string term in terms)
                    if (hay.IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
                if (!all) continue;
            }
            _titleRows.Add(t);
        }
        return _titleRows;
    }
}
