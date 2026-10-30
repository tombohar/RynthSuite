// Ported from Oracle of Dereth by Advis Eveldan (advis61), MIT. See THIRD_PARTY_NOTICES.md.
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RynthCore.Plugin.RynthOracle.Data;

/// <summary>
/// Optional daily check for a newer master quest list at upstream's address (off by default).
/// The download runs on a pool thread and only writes a file; the pump picks the result up
/// (<see cref="TakeResult"/>) and reloads the catalog. <see cref="Shutdown"/> cancels and waits,
/// so nothing of the plugin is left running when it unloads.
/// </summary>
internal sealed class QuestCatalogUpdater
{
    public const string Url = "https://raw.githubusercontent.com/advis61/OracleOfDereth/master/OracleOfDereth/Resources/quests.csv";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private CancellationTokenSource? _cts;
    private Task? _task;
    private volatile string? _result;   // a message for the pump: what happened
    private volatile bool _updated;     // a new file was written

    public bool Running => _task is { IsCompleted: false };

    /// <summary>Starts a check unless one is running. Pump thread.</summary>
    public void Start()
    {
        if (Running) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        _task = Task.Run(() => RunAsync(token));
    }

    /// <summary>Pump thread: the outcome of the last check, once. updated = reload the catalog.</summary>
    public string? TakeResult(out bool updated)
    {
        string? r = _result;
        updated = false;
        if (r == null) return null;
        _result = null;
        updated = _updated;
        _updated = false;
        return r;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RynthOracle/0.1");
            string csv = await http.GetStringAsync(Url, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (!QuestCatalog.Validate(csv, out string why))
            {
                _result = $"The downloaded quest list was not usable ({why}); kept the current one.";
                return;
            }

            string path = QuestCatalog.DownloadedPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && File.ReadAllText(path) == csv)
            {
                _result = "The quest list is up to date.";
                return;
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, csv);
            File.Move(tmp, path, overwrite: true);
            _updated = true;
            _result = "Downloaded a newer quest list.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _result = $"Couldn't check for a newer quest list: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Cancels a running check and waits for it (at most 3 s). Pump thread.</summary>
    public void Shutdown()
    {
        try
        {
            _cts?.Cancel();
            _task?.Wait(TimeSpan.FromSeconds(3));
        }
        catch { }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _task = null;
        }
    }
}
