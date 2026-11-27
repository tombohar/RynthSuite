using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthSort.Sorting;

/// <summary>What a side pack is for besides its rules (0.6.0): items another tool picks.</summary>
internal enum JobKind : byte
{
    None = 0,
    /// <summary>Items RynthAi's loot profile in use marks Salvage (its first matching rule).</summary>
    Salvage = 1,
    /// <summary>Items RynthAi's AutoVendor would sell (its default.utl and its safety checks).</summary>
    Sell = 2,
    /// <summary>Items set aside for a mule: what RynthAi's AutoTrade would give it (its &lt;mule&gt;.utl, else default.utl).</summary>
    Mule = 3,
}

/// <summary>A side pack's job, kept by the pack's id (its number when the id isn't carried, as a rule).</summary>
internal sealed class PackJob
{
    public uint PackId;
    public int Pack = 1;
    public JobKind Kind;
    /// <summary>The mule's character name for <see cref="JobKind.Mule"/> ("" = AutoTrade's default.utl).</summary>
    public string Mule = string.Empty;

    public PackJob Clone() => new() { PackId = PackId, Pack = Pack, Kind = Kind, Mule = Mule };
}

/// <summary>What RynthAi answered for one job: whether there is an answer, and if not, why (the window says it).</summary>
internal sealed class JobAnswer
{
    /// <summary>RynthAi answered: <see cref="Marked"/> is the whole list of items it picks.</summary>
    public bool Known;
    /// <summary>The items it picks (object ids).</summary>
    public readonly HashSet<uint> Marked = new();
    /// <summary>Known: the profile it used ("Hunting.utl"); not known: why ("RynthAi isn't loaded").</summary>
    public string Text = string.Empty;

    public static JobAnswer Unknown(string why) => new() { Text = why };
}

/// <summary>RynthAi's answers for the jobs the carried packs have (the plugin asks; the tests make them).</summary>
internal sealed class JobAnswers
{
    public readonly Dictionary<JobKind, JobAnswer> ByKind = new();
    /// <summary>The mule a Mule pack asked about (one mule at a time: the first Mule pack's).</summary>
    public string Mule = string.Empty;

    public JobAnswer? Of(JobKind kind) => ByKind.TryGetValue(kind, out JobAnswer? a) ? a : null;
}

/// <summary>
/// Pack jobs (0.6.0, #rynth-issues "The bag sort feature is lacking in UX" follow-up): a side pack can
/// hold what another tool picks: Salvage (RynthAi's loot profile), Sell (RynthAi's AutoVendor), or a
/// Mule (RynthAi's AutoTrade). Where an item goes: a name rule first (the player named it), then a
/// job that picks it, then a kind rule. A job pack whose answer isn't known (RynthAi not loaded, too
/// old, no profile) changes nothing: items in it stay, nothing new goes in. Pure.
/// </summary>
internal static class PackJobs
{
    public static string Label(JobKind k) => k switch
    {
        JobKind.Salvage => "Salvage",
        JobKind.Sell => "Sell",
        JobKind.Mule => "Mule",
        _ => "No job",
    };

    /// <summary>"salvage", "sell", "mule": how the file says it.</summary>
    public static string Key(JobKind k) => k switch
    {
        JobKind.Salvage => "salvage",
        JobKind.Sell => "sell",
        JobKind.Mule => "mule",
        _ => string.Empty,
    };

    public static JobKind Parse(string? key) => (key ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "salvage" => JobKind.Salvage,
        "sell" => JobKind.Sell,
        "mule" => JobKind.Mule,
        _ => JobKind.None,
    };

    /// <summary>The side pack (1-based) job <paramref name="j"/> is on: its id when carried, else its number; 0 when neither.</summary>
    public static int Resolve(PackJob j, SortInventory inv)
    {
        int byId = inv.IndexOf(j.PackId);
        if (byId > 0) return byId;
        return j.PackId == 0 && j.Pack >= 1 && j.Pack <= inv.Packs.Count ? j.Pack : 0;
    }

    /// <summary>Each carried side pack's job ([n] for pack n, None when it has none).</summary>
    public static JobKind[] ByPack(IReadOnlyList<PackJob> jobs, SortInventory inv)
    {
        var a = new JobKind[inv.Packs.Count + 1];
        foreach (PackJob j in jobs)
        {
            int n = Resolve(j, inv);
            if (n > 0 && j.Kind != JobKind.None && a[n] == JobKind.None) a[n] = j.Kind;
        }
        return a;
    }

    /// <summary>The job on pack <paramref name="number"/>, or null.</summary>
    public static PackJob? On(IReadOnlyList<PackJob> jobs, SortInventory inv, int number)
    {
        foreach (PackJob j in jobs)
            if (j.Kind != JobKind.None && Resolve(j, inv) == number) return j;
        return null;
    }

    /// <summary>
    /// Sets pack <paramref name="number"/>'s job (None takes it off). One job a pack; the pack is kept
    /// by its id. False when nothing changed or the pack isn't carried.
    /// </summary>
    public static bool Set(List<PackJob> jobs, SortInventory inv, int number, JobKind kind, string? mule)
    {
        if (number < 1 || number > inv.Packs.Count) return false;
        string m = SortRule.Clean(mule);
        PackJob? have = On(jobs, inv, number);
        if (kind == JobKind.None)
        {
            if (have == null) return false;
            jobs.RemoveAll(j => Resolve(j, inv) == number);
            return true;
        }
        if (have != null && have.Kind == kind && (kind != JobKind.Mule || string.Equals(have.Mule, m, StringComparison.Ordinal))) return false;
        jobs.RemoveAll(j => Resolve(j, inv) == number);
        jobs.Add(new PackJob { PackId = inv.Packs[number - 1].Id, Pack = number, Kind = kind, Mule = kind == JobKind.Mule ? m : string.Empty });
        return true;
    }

    /// <summary>The jobs the carried packs have, each once (what to ask RynthAi), and the first Mule pack's mule.</summary>
    public static List<JobKind> Wanted(IReadOnlyList<PackJob> jobs, SortInventory inv, out string mule)
    {
        mule = string.Empty;
        var list = new List<JobKind>();
        bool muleSet = false;
        foreach (PackJob j in jobs)
        {
            if (j.Kind == JobKind.None || Resolve(j, inv) == 0) continue;
            if (!list.Contains(j.Kind)) list.Add(j.Kind);
            if (j.Kind == JobKind.Mule && !muleSet) { mule = j.Mule; muleSet = true; }
        }
        return list;
    }
}

/// <summary>The jobs as the planner sees them for one pass: each carried pack's job and RynthAi's answers.</summary>
internal sealed class JobPlan
{
    /// <summary>[n] = side pack n's job.</summary>
    public JobKind[] Packs = Array.Empty<JobKind>();
    public JobAnswers Answers = new();

    public bool Any
    {
        get
        {
            foreach (JobKind k in Packs) if (k != JobKind.None) return true;
            return false;
        }
    }

    public static JobPlan? For(IReadOnlyList<PackJob> jobs, SortInventory inv, JobAnswers? answers)
    {
        if (jobs.Count == 0) return null;
        var p = new JobPlan { Packs = PackJobs.ByPack(jobs, inv), Answers = answers ?? new JobAnswers() };
        return p.Any ? p : null;
    }

    /// <summary>Pack <paramref name="index"/> has a job whose answer isn't known: what is in it stays.</summary>
    public bool Frozen(int index) =>
        index > 0 && index < Packs.Length && Packs[index] != JobKind.None && Answers.Of(Packs[index])?.Known != true;

    /// <summary>The job packs (pack order) whose job picks <paramref name="it"/>, best first.</summary>
    public IEnumerable<int> PacksFor(SortItem it)
    {
        for (int n = 1; n < Packs.Length; n++)
        {
            JobKind k = Packs[n];
            if (k == JobKind.None) continue;
            JobAnswer? a = Answers.Of(k);
            if (a != null && a.Known && a.Marked.Contains(it.Id)) yield return n;
        }
    }
}
