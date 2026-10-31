using System.Text.Json;
using RynthCore.Plugin.RynthNet;
using RynthCore.Plugin.RynthNet.Characters;

namespace RynthNet.Tests;

// The Characters window's two looks (2026-10-31): minimal (one line per character, frameless) and
// full (every column), the low-health colours, the status dot, the minimal line's layout, /rn view,
// the frameless manifest, and the choice kept per character in settings.json.
internal static partial class Program
{
    private static void LookChecks()
    {
        HealthThresholds();
        StatusDots();
        MinimalLayout();
        ViewNames();
        ViewCommand();
        FramelessManifest();
        ViewPersistence();
    }

    private static CharacterRow Row(float hp, CharStateKind kind = CharStateKind.Idle, bool vitals = true) => new()
    {
        Name = "Char One", HasVitals = vitals, Hp = new CharVital(hp, "x"), StateKind = kind,
    };

    private static void HealthThresholds()
    {
        Check(CharactersLook.LowHp == 0.5f && CharactersLook.CriticalHp == 0.25f, "low at half health, critical at a quarter");
        Check(CharactersLook.Hp(1f) == HpWarn.Ok && CharactersLook.Hp(0.51f) == HpWarn.Ok, "over half: fine (the text colour)");
        Check(CharactersLook.Hp(0.5f) == HpWarn.Low && CharactersLook.Hp(0.26f) == HpWarn.Low, "half or less: low (amber)");
        Check(CharactersLook.Hp(0.25f) == HpWarn.Critical && CharactersLook.Hp(0.01f) == HpWarn.Critical, "a quarter or less: critical (red)");
        Check(CharactersLook.Hp(0f) == HpWarn.Dead, "no health: dead (red)");
        Check(CharactersLook.Hp(Row(0.9f, CharStateKind.Dead)) == HpWarn.Dead, "a dead state is dead, whatever the last vitals said");
        Check(CharactersLook.Hp(Row(0.3f)) == HpWarn.Low && CharactersLook.Hp(Row(0.2f)) == HpWarn.Critical, "a row's health share");
        Check(CharactersLook.Hp(Row(0f, vitals: false)) == HpWarn.Unknown, "no vitals yet: unknown, not dead");
    }

    private static void StatusDots()
    {
        Check(CharactersLook.Dot(new CharacterRow { LoggedOut = true, StateKind = CharStateKind.LoggedOut }) == CharDot.Off, "logged out: an empty ring");
        Check(CharactersLook.Dot(new CharacterRow { StateKind = CharStateKind.NoStatus }) == CharDot.Off, "no status yet: an empty ring");
        Check(CharactersLook.Dot(new CharacterRow { IsSelf = true }) == CharDot.Self, "this client");
        Check(CharactersLook.Dot(new CharacterRow { StateKind = CharStateKind.Dead, Stale = true }) == CharDot.Dead, "dead before stale");
        Check(CharactersLook.Dot(new CharacterRow { StateKind = CharStateKind.Botting, Stale = true }) == CharDot.Stale, "stale before what the bot was doing");
        Check(CharactersLook.Dot(new CharacterRow { StateKind = CharStateKind.Botting }) == CharDot.Botting, "botting");
        Check(CharactersLook.Dot(new CharacterRow { StateKind = CharStateKind.Fighting }) == CharDot.Fighting, "botting: fighting");
        Check(CharactersLook.Dot(new CharacterRow { StateKind = CharStateKind.Portal }) == CharDot.Portal, "portal space");
        Check(CharactersLook.Dot(new CharacterRow { StateKind = CharStateKind.Idle }) == CharDot.Idle
              && CharactersLook.Dot(new CharacterRow { StateKind = CharStateKind.CombatMode }) == CharDot.Idle, "idle, in or out of a stance");
    }

    private static void MinimalLayout()
    {
        // dot 12, gap 6, bars 3 x 24 + 2 x 2 = 76, arrow 14: everything for an 80 px name and a 40 px distance is 234 px.
        MinimalLine all = CharactersLook.Minimal(300, 80, 40);
        Check(all.Bars && all.Distance && all.NameWidth == 80 && all.BarWidth == 24, "300 px: name, bars, arrow and distance");
        Check(Math.Abs(all.Width(1f, 40) - 234) < 0.01f, $"the whole line is 234 px (got {all.Width(1f, 40)})");
        Check(CharactersLook.Minimal(234, 80, 40).Distance && !CharactersLook.Minimal(233, 80, 40).Distance, "the distance needs the whole line");
        MinimalLine noDist = CharactersLook.Minimal(200, 80, 40);
        Check(noDist.Bars && !noDist.Distance && noDist.NameWidth == 80, "200 px: name and bars, no distance");
        MinimalLine squeezed = CharactersLook.Minimal(150, 80, 40);
        Check(squeezed.Bars && squeezed.NameWidth == 56 && squeezed.BarWidth == 24, $"150 px: the name gives way first ({squeezed.NameWidth})");
        MinimalLine thin = CharactersLook.Minimal(100, 80, 40);
        Check(thin.Bars && thin.NameWidth == 30 && thin.BarWidth == 16, $"100 px: the name at its least, then the bars narrow ({thin.BarWidth})");
        MinimalLine none = CharactersLook.Minimal(60, 80, 40);
        Check(!none.Bars && !none.Distance && none.NameWidth == 48, "60 px: just the dot and the name");
        Check(!CharactersLook.Minimal(1000, 80, 0).Distance, "no row has a distance: no distance column");
        MinimalLine big = CharactersLook.Minimal(600, 160, 80, k: 2f);
        Check(big.Bars && big.Distance && big.BarWidth == 48, "the sizes grow with the text size");
        Check(CharactersLook.MinimalHeight(0) == 20 && CharactersLook.MinimalHeight(3) == 52 && CharactersLook.MinimalHeight(3, 1.5f) == 78,
            "the window asks for one line per character (one for the status when there are none)");
    }

    private static void ViewNames()
    {
        Check(CharactersLook.Name(CharactersViewMode.Minimal) == "minimal" && CharactersLook.Name(CharactersViewMode.Full) == "full", "names");
        foreach (string s in new[] { "minimal", "MIN", "compact", "bare" })
            Check(CharactersLook.TryParse(s, out CharactersViewMode m) && m == CharactersViewMode.Minimal, $"'{s}' is minimal");
        foreach (string s in new[] { "full", "Edit", "framed" })
            Check(CharactersLook.TryParse(s, out CharactersViewMode m) && m == CharactersViewMode.Full, $"'{s}' is full");
        Check(!CharactersLook.TryParse("huge", out _) && !CharactersLook.TryParse(null, out _), "anything else isn't a view");
        Check(CharactersLook.FromSetting(null) == CharactersViewMode.Minimal && CharactersLook.FromSetting("weird") == CharactersViewMode.Minimal
              && CharactersLook.FromSetting("full") == CharactersViewMode.Full, "the setting: minimal unless it says full");
    }

    private static void ViewCommand()
    {
        var (min, full) = (CharactersViewMode.Minimal, CharactersViewMode.Full);
        ViewAnswer a = CharactersLook.Command("", min, loggedIn: false);
        Check(a.Ok && !a.Changed && a.Next == min && a.Reply.StartsWith("the Characters window is in the minimal view", StringComparison.Ordinal),
            "/rn view alone: the view now (logged in or not): " + a.Reply);
        a = CharactersLook.Command("full", min, true);
        Check(a.Ok && a.Changed && a.Next == full && a.Reply.StartsWith("the Characters window now shows the full view", StringComparison.Ordinal), "/rn view full: " + a.Reply);
        a = CharactersLook.Command("FULL", full, true);
        Check(a.Ok && !a.Changed && a.Reply.StartsWith("the Characters window is already in the full view", StringComparison.Ordinal), "already full: " + a.Reply);
        Check(CharactersLook.Command("toggle", full, true).Next == min && CharactersLook.Command("toggle", min, true).Next == full, "toggle");
        Check(CharactersLook.Command("minimal", full, true).Reply.Contains("/rn view full", StringComparison.Ordinal), "the minimal reply says how to get the full view back");
        a = CharactersLook.Command("full", min, loggedIn: false);
        Check(!a.Ok && a.Reply.StartsWith("log in first", StringComparison.Ordinal), "before login: why (it is kept per character)");
        a = CharactersLook.Command("huge", min, true);
        Check(!a.Ok && a.Reply == CharactersLook.Usage && a.Reply.StartsWith("usage: /rn view", StringComparison.Ordinal), "a wrong view: the usage");
    }

    private static void FramelessManifest()
    {
        Check(CharactersManifest.For(false) == CharactersManifest.Json && CharactersManifest.For(true) == CharactersManifest.FramelessJson,
            "the engine framed manifest without frameless windows, the frameless one with them");
        using JsonDocument doc = JsonDocument.Parse(CharactersManifest.FramelessJson);
        JsonElement w = doc.RootElement.GetProperty("windows")[0];
        Check(w.GetProperty("title").GetString() == CharactersManifest.Title && w.GetProperty("surface").GetString() == CharactersManifest.Surface,
            "the same title (players' saved place) and surface");
        Check(w.GetProperty("chrome").GetString() == "none" && w.GetProperty("fitHeight").GetBoolean(), "frameless, as tall as what it shows");
        Check(w.GetProperty("bar").GetBoolean() && w.GetProperty("minWidth").GetInt32() <= 200, "a bar button; narrow enough for the minimal line");
        using JsonDocument old = JsonDocument.Parse(CharactersManifest.Json);
        Check(!old.RootElement.GetProperty("windows")[0].TryGetProperty("chrome", out _), "the framed one has no chrome key (older engines)");
    }

    private static void ViewPersistence()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "settings-test-" + Environment.ProcessId);
        string? before = Environment.GetEnvironmentVariable("RYNTHNET_DIR");
        Environment.SetEnvironmentVariable("RYNTHNET_DIR", dir);
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(RynthNetSettings.FilePath)) File.Delete(RynthNetSettings.FilePath);
            Check(RynthNetSettings.FilePath.StartsWith(dir, StringComparison.OrdinalIgnoreCase), "the test's settings file is under its own bin folder");

            RynthNetSettings fresh = RynthNetSettings.Load(out string? err);
            Check(err == null && fresh.ViewFor("TestWorld|Char One") == CharactersViewMode.Minimal, "nothing saved: minimal (the default)");

            const string a = "TestWorld|Char One", b = "TestWorld|Char Two";
            Check(RynthNetSettings.Update(s => s.Character(a).View = CharactersLook.Name(CharactersViewMode.Full), out _, out err), "saved: " + err);
            RynthNetSettings back = RynthNetSettings.Load(out err);
            Check(back.ViewFor(a) == CharactersViewMode.Full, "the full view comes back for that character");
            Check(back.ViewFor(b) == CharactersViewMode.Minimal, "another character keeps the default");
            string text = File.ReadAllText(RynthNetSettings.FilePath);
            Check(text.Contains("\"view\": \"full\"", StringComparison.Ordinal), "written as \"view\": \"full\" under the character");

            // Another client's change (tags) on top keeps the view, and the view keeps the tags.
            Check(RynthNetSettings.Update(s => s.Character(a).Tags.Add("heal"), out _, out _), "a tag saved alongside");
            back = RynthNetSettings.Load(out _);
            Check(back.ViewFor(a) == CharactersViewMode.Full && back.TagsFor(a).Contains("heal"), "the view and the tags live together");

            // Back to minimal: saved as such; an entry with nothing left is dropped.
            Check(RynthNetSettings.Update(s => { s.Character(b).View = "minimal"; s.Character(a).Tags.Clear(); s.Character(a).View = null; }, out _, out _), "minimal saved");
            back = RynthNetSettings.Load(out _);
            Check(back.ViewFor(b) == CharactersViewMode.Minimal && back.Characters.ContainsKey(b), "minimal is kept when chosen");
            Check(!back.Characters.ContainsKey(a), "a character with no tags, policy or view isn't written");

            // A hand-edited file with a view the plugin doesn't know: the default, not an error.
            File.WriteAllText(RynthNetSettings.FilePath, "{\"characters\":{\"TestWorld|Char One\":{\"tags\":[],\"view\":\"sideways\"}}}");
            back = RynthNetSettings.Load(out err);
            Check(err == null && back.ViewFor(a) == CharactersViewMode.Minimal && back.Characters[a].View == null, "an unknown view: minimal");
            File.WriteAllText(RynthNetSettings.FilePath, "{\"characters\":{\"TestWorld|Char One\":{\"view\":\"FULL\"}}}");
            Check(RynthNetSettings.Load(out _).ViewFor(a) == CharactersViewMode.Full, "any case");
        }
        finally
        {
            Environment.SetEnvironmentVariable("RYNTHNET_DIR", before);
            try { Directory.Delete(dir, recursive: true); } catch { }   // the test's own output, under bin
        }
    }
}
