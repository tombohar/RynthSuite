using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RynthCore.Plugin.RynthInventory.Data;
using RynthCore.Plugin.RynthInventory.Scanning;
using RynthCore.Plugin.RynthInventory.Ui;
using RynthCore.Plugin.RynthInventory.Views;
using RynthCore.Plugin.Shared;
using RynthCore.PluginCore;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthInventory;

/// <summary>
/// RynthInventory: a searchable inventory across all of a player's characters on every server
/// (like Virindi Global Inventory). Each client scans the character it is logged into (a full
/// scan after login, then whenever the inventory changes, debounced) and writes one file per
/// character per server; storage the character opens (house storage, banks, vaults) is
/// recorded when it is opened, and storage another plugin reads for it (a server plugin's
/// server-side store, through Plugins/Shared/StorageFeed.cs) when that arrives. /ginv opens a
/// window that merges every file.
///
/// Everything runs on the plugin pump (OnTick and the events the engine drains into it)
/// except OnChatBarEnter, which the engine may call on AC's thread: it only queues the
/// command. File reads for the merged view and file writes run on worker tasks; they touch
/// no game state.
/// </summary>
public sealed class RynthInventoryPlugin : RynthPluginBase
{
    // Previous release: 0.1.0. Keep in step with PluginExports.GetVersion.
    public const string Version = "0.1.1";

    private const long IdentityRetryMs = 1000;
    private const long AccountWaitMs = 15_000;
    private const long FileCheckMs = 5000;
    private const long StorageFirstScanMs = 700;
    private const long StorageRescanMs = 3000;
    private const long StorageSettleScans = 3;
    /// <summary>An online character's file is rewritten at least this often, so its "scanned" time stays honest.</summary>
    private const long RefreshWriteMs = 10 * 60_000;
    /// <summary>Within this long after login an empty scan means "not loaded yet", never "owns nothing".</summary>
    private const long EmptyScanGraceMs = 90_000;

    private readonly InventoryContext _c = new();
    private readonly InventoryScanner _scanner = new();
    private readonly ScanScheduler _scheduler = new();
    private readonly UiHost _ui = new();
    private UiWindow? _window;
    private InventoryView? _view;
    private readonly ConcurrentQueue<string> _commands = new();

    // Storage other plugins read for us ("storage" plugin commands, StorageFeed.cs): queued by
    // ApplyRemoteCommand (any thread), filed on the tick once the character is known. Aelrynth's
    // account vault comes this way from RynthAelrynth (it was read here until 2026-10-20).
    private readonly ConcurrentQueue<string> _storagePushes = new();
    private const int MaxQueuedPushes = 8;
    private string _lastPush = string.Empty;

    private bool _identityKnown;
    private long _loginMs;
    private long _nextIdentityTryMs;
    private bool _oldAccountChecked;
    private bool _forceWrite;

    // Write in flight (one at a time).
    private Task? _writeTask;
    private long _lastWriteMs;

    // Storage being watched.
    private uint _lastViewed;
    private uint _openStorage;
    private string _openStorageName = string.Empty;
    private string _openStorageLabel = string.Empty;
    private uint _openStorageCell;
    private long _nextStorageScanMs;
    private int _storageScans;

    // Merged view: files parsed once and re-read only when they change.
    private Dictionary<string, (InventoryFileStamp Stamp, CharacterSnapshot? Snapshot)> _files = new(StringComparer.OrdinalIgnoreCase);
    private Task<(Dictionary<string, (InventoryFileStamp, CharacterSnapshot?)> Files, int Broken)>? _loadTask;
    private long _nextFileCheckMs;
    private bool _indexDirty = true;
    private bool _reloadWanted = true;

    public override int Initialize()
    {
        _c.Host = Host;
        _c.Reply = new CommandReply("[RynthInventory]", (t, c) => Host.WriteToChat(t, c), t => Host.Log(t));
        _c.Settings.Load();
        _c.RequestRescan = () => { _scheduler.RequestFullScan(); _forceWrite = true; };
        _c.RequestReload = () => { _reloadWanted = true; _files.Clear(); };
        _c.Forget = ForgetCharacter;
        _ui.Log = msg => Host.Log(msg);

        _view = new InventoryView(_c);
        // No bar button of its own: opened from the engine Inventory window's GInv button, the
        // Scripts menu or /ginv.
        _window = _ui.Add(new UiWindow("RynthInventory/Main", "Global Inventory")
        {
            DefaultWidth = 640f, DefaultHeight = 600f, MinWidth = 420f, MinHeight = 300f,
            BarButton = false,
        });

        Host.Log($"[RynthInventory] {Version} initialized. /ginv opens the window. Files: {_c.Store.Root}");
        return 0;
    }

    // The engine calls this on a real login and on a freshly (re)loaded plugin already in game.
    public override void OnLoginComplete()
    {
        _c.InWorld = true;
        _c.Settings.Load();
        _identityKnown = false;
        _oldAccountChecked = false;
        _loginMs = Environment.TickCount64;
        _nextIdentityTryMs = _loginMs;
        _c.Live = null;
        _c.LastSavedUtc = default;
        _c.LastSaveError = string.Empty;
        _scanner.Reset();
        CloseStorage();
        _lastViewed = 0;
        _lastWriteMs = 0;
        _carriedIds.Clear();
        _carriedPacks.Clear();
        RefreshIconSupport();
        if (_window != null) { _window.Visible = _c.Settings.MainWindowOpen; _window.NeedsPass = true; }
    }

    public override void OnLogout()
    {
        // Last word for this character: write what it has now (also refreshes its "scanned" time).
        FlushNow(final: true);
        _c.InWorld = false;
        _identityKnown = false;
        _scheduler.Logout();
        CloseStorage();
        if (_window != null) { _window.Visible = false; _window.NeedsPass = true; }
        _ui.SubmitEmpty(Host);
        _c.Live = null;
        _indexDirty = true;
        while (_storagePushes.TryDequeue(out _)) { }   // the next character's come fresh
        _lastPush = string.Empty;
    }

    public override void Shutdown()
    {
        FlushNow(final: true);
        _ui.SubmitEmpty(Host);
        try { _loadTask?.Wait(2000); } catch { }
    }

    // ── Events (pump thread) ────────────────────────────────────────────────

    public override void OnCreateObject(uint objectId)
    {
        if (!_identityKnown || _c.Player == 0) return;
        // Only objects that landed in the character's inventory matter (most creates are the landscape).
        if (Host.TryGetObjectOwnershipInfo(objectId, out uint container, out uint wielder, out _)
            && (container == _c.Player || wielder == _c.Player || IsCarriedPack(container)))
            _scheduler.InventoryEvent(Environment.TickCount64);
    }

    public override void OnDeleteObject(uint objectId)
    {
        if (!_identityKnown) return;
        if (_carriedIds.Contains(objectId)) _scheduler.InventoryEvent(Environment.TickCount64);
    }

    public override void OnUpdateObjectInventory(uint objectId)
    {
        if (!_identityKnown) return;
        long now = Environment.TickCount64;
        if (objectId == _openStorage && _openStorage != 0)
            _nextStorageScanMs = Math.Min(_nextStorageScanMs, now + 300);
        else if (objectId == _c.Player || IsCarriedPack(objectId))
            _scheduler.InventoryEvent(now);
    }

    public override void OnViewObjectContents(uint objectId)
    {
        if (!_identityKnown || objectId == 0) return;
        _lastViewed = objectId;
        bool kept = _c.Settings.IsKept(_c.Server, objectId);
        if (!InventoryScanner.IsStorage(Host, objectId, _c.Player, _c.Settings.StorageWordList(), kept, out string name, out string why))
        {
            if (why == "carried") _scheduler.InventoryEvent(Environment.TickCount64);
            return;
        }
        BeginStorage(objectId, name, why);
    }

    public override void OnStopViewingObjectContents(uint objectId)
    {
        if (objectId != 0 && objectId == _openStorage)
        {
            ScanStorage(Environment.TickCount64, final: true);
            CloseStorage();
        }
    }

    // ── Chat ────────────────────────────────────────────────────────────────

    // May run on AC's thread: classify and queue only.
    public override void OnChatBarEnter(string? text, ref int eat)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string t = text.Trim();
        if (!t.StartsWith("/ginv", StringComparison.OrdinalIgnoreCase) || (t.Length > 5 && !char.IsWhiteSpace(t[5])))
            return;
        eat = 1;
        _commands.Enqueue(t.Length > 5 ? t.Substring(5).Trim() : string.Empty);
    }

    private void RunCommand(string arg)
    {
        // Every /ginv answers in chat (2026-10-28, InventoryCommand.cs; Plugins/Shared/CommandReply.cs).
        // RynthInventory can't tell a typed /ginv from a meta's, so each one does.
        InventoryCommand c = InventoryCommand.Parse(arg);
        switch (c.Verb)
        {
            case InventoryVerb.Window:
                if (_window != null)
                {
                    _window.Visible = !_window.Visible; _window.NeedsPass = true;
                    if (Host.HasUi) _c.Print(InventoryCommand.Window(_window.Visible));
                }
                break;
            case InventoryVerb.Scan:
                _scheduler.RequestFullScan();
                _forceWrite = true;
                _c.Print("Rescanning this character; it saves within a few seconds.");
                break;
            case InventoryVerb.Keep:
                KeepOpenContainer();
                break;
            case InventoryVerb.Reload:
                _reloadWanted = true;
                _files.Clear();
                _c.Print("Reading the inventory files again.");
                break;
            case InventoryVerb.Status:
                PrintStatus();
                break;
            case InventoryVerb.Help:
                _c.Print(InventoryCommand.Usage);
                break;
            default:
                _view?.SetSearch(c.Text);
                if (_window != null) { _window.Visible = true; _window.NeedsPass = true; }
                if (Host.HasUi) _c.Print(InventoryCommand.Searching(c.Text));
                break;
        }
        // Without plugin windows the window verbs say why nothing shows (every time).
        if (_window != null && _window.Visible && !Host.HasUi && c.Verb is InventoryVerb.Window or InventoryVerb.Search)
            _c.PrintFail(InventoryCommand.NoWindows);
    }

    private void PrintStatus()
    {
        string who = _identityKnown ? $"{_c.Character} on {_c.Server} (account {_c.Account})" : "no character yet";
        int items = _c.Live?.Items.Count ?? 0, storage = _c.Live?.Storage.Count ?? 0;
        string saved = _c.LastSavedUtc == default ? "not saved yet" : "saved " + InventoryIndex.Age(_c.LastSavedUtc, DateTime.UtcNow);
        _c.Print($"{who}: {items} items carried, {storage} storage container(s), {saved}. {_c.Index?.Characters.Count ?? 0} characters in {_c.Store.Root}.");
        if (_lastPush.Length > 0) _c.Print("From other plugins: " + _lastPush + ".");
        if (_c.LastSaveError.Length > 0) _c.Print("Last save failed: " + _c.LastSaveError);
    }

    private void KeepOpenContainer()
    {
        if (!_identityKnown || _lastViewed == 0)
        {
            _c.PrintFail("Open the container first, then type /ginv keep while it is open.");
            return;
        }
        string name = Host.TryGetObjectName(_lastViewed, out string n) ? n : "container";
        if (!InventoryScanner.IsStorage(Host, _lastViewed, _c.Player, Array.Empty<string>(), kept: true, out name, out string why))
        {
            _c.PrintFail($"{name} can't be kept as storage ({why}).");
            return;
        }
        string key = InventorySettings.KeepKey(_c.Server, _lastViewed);
        if (!_c.Settings.KeptStorage.Contains(key))
        {
            _c.Settings.KeptStorage.Add(key);
            _c.Settings.Save();
        }
        _c.Print($"{name} will be recorded as storage whenever it is opened.");
        BeginStorage(_lastViewed, name, why);
    }

    // ── Tick ────────────────────────────────────────────────────────────────

    public override void OnTick()
    {
        long now = Environment.TickCount64;
        _c.UtcNow = DateTime.UtcNow;

        while (_commands.TryDequeue(out string? cmd)) RunCommand(cmd);
        _c.Reply?.Flush();
        _ui.Poll(Host);

        if (_c.InWorld)
        {
            try { TickScan(now); }
            catch (Exception ex)
            {
                Host.Log($"[RynthInventory] scan failed: {ex.GetType().Name}: {ex.Message}");
                _scheduler.FullScanDone(now, _c.Live?.ContentHash() ?? 0, 0);   // don't retry every tick
            }
        }
        TickWrite(now);
        TickIndex(now);

        if (!Host.HasUi || _window == null || _view == null) return;
        Record(_window, now, 2000, _view.Draw);
        _ui.Submit(Host, now);

        if (_c.InWorld && _window.Visible != _c.Settings.MainWindowOpen)
        {
            _c.Settings.MainWindowOpen = _window.Visible;
            _c.Settings.Save();
        }
    }

    private void TickScan(long now)
    {
        if (!_identityKnown)
        {
            if (now < _nextIdentityTryMs) return;
            _nextIdentityTryMs = now + IdentityRetryMs;
            if (!InventoryScanner.TryReadIdentity(Host, out uint player, out string server, out string account, out string character))
                return;
            // The engine learns the account name on AC's thread during login; give it a moment
            // so the file lands in the account's folder rather than "unknown".
            if (account == "unknown" && now - _loginMs < AccountWaitMs)
                return;
            StartCharacter(now, player, server, account, character);
        }

        _scanner.WarmUp(Host, _c.Player);
        if (_scheduler.ShouldFullScan(now))
            FullScan(now);
        else if (_scheduler.ShouldPoll(now))
            _scheduler.PollResult(now, _scanner.Fingerprint(Host, _c.Player));

        if (_openStorage != 0 && now >= _nextStorageScanMs)
            ScanStorage(now, final: false);

        TickStoragePushes(now);
    }

    // ── Storage from other plugins ──────────────────────────────────────────

    /// <summary>
    /// RynthPluginApplyRemoteCommand: "storage" (StorageFeed.Action) carries a whole store another
    /// plugin read. Any thread: the payload is only queued (the newest few).
    /// </summary>
    internal void ApplyRemoteCommand(string action, string value)
    {
        if (!action.Equals(StorageFeed.Action, StringComparison.OrdinalIgnoreCase) || value.Length == 0 || value.Length > StorageFeed.MaxJsonLength) return;
        _storagePushes.Enqueue(value);
        while (_storagePushes.Count > MaxQueuedPushes && _storagePushes.TryDequeue(out _)) { }
    }

    /// <summary>Files what other plugins sent, once the character (and so its storage) is known. Plugin tick.</summary>
    private void TickStoragePushes(long now)
    {
        if (_c.Live == null) return;
        while (_storagePushes.TryDequeue(out string? json))
        {
            StorageFeedRecord? feed = StorageFeed.Parse(json, out string error);
            if (feed == null)
            {
                Host.Log("[RynthInventory] storage from another plugin refused: " + error);
                continue;
            }
            StorageRecord record = ExternalStorage.ToRecord(feed, DateTime.UtcNow);
            CharacterSnapshot snap = NewSnapshot(_c.Live.Items, ExternalStorage.FileInto(_c.Live.Storage, record));
            snap.ScannedUtc = _c.Live.ScannedUtc;
            _c.Live = snap;
            _indexDirty = true;
            _scheduler.ContentChanged(now, snap.ContentHash());
            _lastPush = $"{record.Label} ({record.Items.Count} item(s)) at {DateTime.Now:HH:mm}";
        }
    }

    private void StartCharacter(long now, uint player, string server, string account, string character)
    {
        _identityKnown = true;
        _c.Player = player;
        _c.Server = server;
        _c.Account = account;
        _c.Character = character;
        _scheduler.Login(now);

        // Start from this character's saved file: it keeps the storage seen on earlier visits.
        string path = _c.Store.PathFor(server, account, character);
        CharacterSnapshot? saved = InventoryStore.Load(path, out _);
        if (saved == null && account != "unknown")
            saved = InventoryStore.Load(_c.Store.PathFor(server, "unknown", character), out _);
        if (saved != null && InventoryIndex.SameName(saved.Character, character))
        {
            saved.Account = account;
            saved.CharacterId = player;
            _c.Live = saved;
            if (saved.FilePath == path) _scheduler.KnownSaved(saved.ContentHash());
        }
        _indexDirty = true;
        Host.Log($"[RynthInventory] tracking {character} on {server} (account {account}); file {path}");
    }

    private readonly HashSet<uint> _carriedIds = new();
    private readonly HashSet<uint> _carriedPacks = new();

    private bool IsCarriedPack(uint id) => id != 0 && _carriedPacks.Contains(id);

    private void FullScan(long now)
    {
        List<ItemRecord> items = _scanner.ScanCarried(Host, _c.Player);
        ulong fingerprint = _scanner.Fingerprint(Host, _c.Player);
        if (items.Count == 0 && now - _loginMs < EmptyScanGraceMs)
        {
            // The client hasn't got the inventory yet; never save "owns nothing" for that.
            _scheduler.FullScanDone(now, _c.Live?.ContentHash() ?? 0, fingerprint);
            return;
        }

        _carriedIds.Clear();
        _carriedPacks.Clear();
        foreach (ItemRecord it in items)
        {
            _carriedIds.Add(it.Id);
            if (it.Place == ItemPlace.Pack && it.Path.Length == 1 && it.Category == ItemCategory.Container) _carriedPacks.Add(it.Id);
        }

        var snap = NewSnapshot(items, _c.Live?.Storage ?? new List<StorageRecord>());
        snap.ScannedUtc = DateTime.UtcNow;
        _c.Live = snap;
        _indexDirty = true;
        _scheduler.FullScanDone(now, snap.ContentHash(), fingerprint);
    }

    private CharacterSnapshot NewSnapshot(List<ItemRecord> items, List<StorageRecord> storage) => new()
    {
        Server = _c.Server,
        Account = _c.Account,
        Character = _c.Character,
        CharacterId = _c.Player,
        ScannedUtc = _c.Live?.ScannedUtc ?? default,
        PluginVersion = Version,
        Items = items,
        Storage = new List<StorageRecord>(storage),
    };

    // ── Storage ─────────────────────────────────────────────────────────────

    private void BeginStorage(uint id, string name, string why)
    {
        _openStorage = id;
        _openStorageName = name;
        _openStorageLabel = InventoryScanner.StorageLabel(Host, id, name, out _openStorageCell);
        _storageScans = 0;
        _nextStorageScanMs = Environment.TickCount64 + StorageFirstScanMs;
        _c.OpenStorage = id;
        _c.OpenStorageLabel = _openStorageLabel;
        Host.Log($"[RynthInventory] recording {_openStorageLabel} (0x{id:X8}): {why}");
    }

    private void CloseStorage()
    {
        _openStorage = 0;
        _c.OpenStorage = 0;
        _c.OpenStorageLabel = string.Empty;
    }

    private void ScanStorage(long now, bool final)
    {
        if (_openStorage == 0 || _c.Live == null) { _nextStorageScanMs = now + StorageRescanMs; return; }
        List<ItemRecord> items = _scanner.ScanContainer(Host, _openStorage);
        _storageScans++;
        _nextStorageScanMs = now + (_storageScans < StorageSettleScans ? 1500 : StorageRescanMs);
        // Contents arrive a moment after the container opens, and are gone right after it
        // closes: an empty read is only believed once the container has been open a while.
        if (items.Count == 0 && (final || _storageScans < StorageSettleScans)) return;

        var record = new StorageRecord
        {
            Id = _openStorage,
            Name = _openStorageName,
            Label = _openStorageLabel,
            Cell = _openStorageCell,
            SeenUtc = DateTime.UtcNow,
            Items = items,
        };
        var storage = new List<StorageRecord>(_c.Live.Storage);
        int i = storage.FindIndex(s => s.Id == record.Id);
        if (i >= 0) storage[i] = record; else storage.Add(record);
        var snap = NewSnapshot(_c.Live.Items, storage);
        _c.Live = snap;
        _indexDirty = true;
        _scheduler.ContentChanged(now, snap.ContentHash());
    }

    // ── Writing ─────────────────────────────────────────────────────────────

    private void TickWrite(long now)
    {
        if (_writeTask != null)
        {
            if (!_writeTask.IsCompleted) return;
            _writeTask = null;
        }
        if (!_identityKnown || _c.Live == null || !_scheduler.Active) return;
        // Rewrite now and then even when nothing changed, so other clients see this character
        // as current (once a minute after login, then every RefreshWriteMs).
        bool refresh = _lastWriteMs == 0 ? now - _loginMs >= 60_000 : now - _lastWriteMs >= RefreshWriteMs;
        if (!_forceWrite && !refresh && !_scheduler.ShouldWrite(now)) return;
        if (_forceWrite && _scheduler.ShouldFullScan(now)) return;   // /ginv scan: write after the scan
        _forceWrite = false;
        StartWrite(now, _c.Live);
    }

    private void StartWrite(long now, CharacterSnapshot snap)
    {
        // The worker only reads the snapshot; published snapshots are never changed (a change makes a new one).
        if (snap.ScannedUtc == default) snap.ScannedUtc = DateTime.UtcNow;
        ulong hash = snap.ContentHash();
        _lastWriteMs = now;
        InventoryStore store = _c.Store;
        bool checkOld = !_oldAccountChecked && snap.Account != "unknown";
        _oldAccountChecked = true;
        string server = snap.Server, character = snap.Character;
        _writeTask = Task.Run(() =>
        {
            try
            {
                store.Save(snap);
                if (checkOld)
                {
                    // The same character saved earlier without an account name: that file is superseded.
                    string old = store.PathFor(server, "unknown", character);
                    if (File.Exists(old)) store.Forget(old, out _);
                }
                return string.Empty;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }).ContinueWith(t =>
        {
            string error = t.IsFaulted ? (t.Exception?.GetBaseException().Message ?? "failed") : t.Result;
            _writeResult = new WriteResult(hash, error);
        }, TaskScheduler.Default);
    }

    private sealed record WriteResult(ulong Hash, string Error);
    private volatile WriteResult? _writeResult;

    private void TakeWriteResult(long now)
    {
        WriteResult? r = _writeResult;
        if (r == null) return;
        _writeResult = null;
        if (r.Error.Length == 0)
        {
            _scheduler.Written(r.Hash);
            _c.LastSavedUtc = DateTime.UtcNow;
            _c.LastSaveError = string.Empty;
            _reloadWanted = true;   // other views pick the new file up; ours already has it live
        }
        else
        {
            _scheduler.WriteFailed(now);
            _c.LastSaveError = r.Error;
            Host.Log("[RynthInventory] save failed: " + r.Error);
        }
    }

    /// <summary>Logout and shutdown: write now, on this thread, if anything is unsaved (or always at logout).</summary>
    private void FlushNow(bool final)
    {
        try
        {
            try { _writeTask?.Wait(3000); } catch { }
            _writeTask = null;
            TakeWriteResult(Environment.TickCount64);
            if (!_identityKnown || _c.Live == null) return;
            if (!final && !_scheduler.Dirty) return;
            CharacterSnapshot snap = _c.Live;
            if (snap.ScannedUtc == default) snap.ScannedUtc = DateTime.UtcNow;
            _c.Store.Save(snap);
            _scheduler.Written(snap.ContentHash());
            _c.LastSavedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            try { Host.Log("[RynthInventory] final save failed: " + ex.Message); } catch { }
        }
    }

    private string ForgetCharacter(CharacterInfo ci)
    {
        int moved = 0;
        string? error = null;
        var paths = new List<string>(ci.ShadowedFiles);
        if (!string.IsNullOrEmpty(ci.Snapshot.FilePath)) paths.Add(ci.Snapshot.FilePath);
        else paths.Add(_c.Store.PathFor(ci.Server, ci.Account, ci.Name));   // the live one: its own file
        foreach (string p in paths)
        {
            if (_c.Store.Forget(p, out string? e)) moved++;
            else if (e != "already gone") error = e;
        }
        _reloadWanted = true;
        _files.Clear();
        if (ci.IsCurrentCharacter)
        {
            // Back at the next write; start from a clean slate so the old storage isn't re-saved.
            if (_c.Live != null) _c.Live = NewSnapshot(_c.Live.Items, new List<StorageRecord>());
            _scheduler.ContentChanged(Environment.TickCount64, 1);
        }
        _indexDirty = true;
        if (moved > 0) return $"Forgot {ci.Name} on {ci.Server} (moved to {InventoryStore.ForgottenFolder}).";
        return error != null ? $"Couldn't forget {ci.Name}: {error}" : $"{ci.Name} had no file to move.";
    }

    // ── Merged view ─────────────────────────────────────────────────────────

    private void TickIndex(long now)
    {
        TakeWriteResult(now);

        if (_loadTask != null && _loadTask.IsCompleted)
        {
            try
            {
                var (files, broken) = _loadTask.Result;
                _files = new Dictionary<string, (InventoryFileStamp, CharacterSnapshot?)>(files, StringComparer.OrdinalIgnoreCase);
                _c.BrokenFiles = broken;
            }
            catch (Exception ex)
            {
                Host.Log("[RynthInventory] reading files failed: " + ex.Message);
            }
            _loadTask = null;
            _c.Loading = false;
            _indexDirty = true;
        }

        // Files are only listed and read while the window is open (and once at start).
        bool visible = _window?.Visible ?? false;
        if (_loadTask == null && (visible || _c.Index == null) && (_reloadWanted || now >= _nextFileCheckMs))
        {
            _nextFileCheckMs = now + FileCheckMs;
            _reloadWanted = false;
            StartLoad();
        }

        if (_indexDirty && (visible || _c.Index == null) && _loadTask == null)
        {
            _indexDirty = false;
            var snaps = new List<CharacterSnapshot>();
            foreach (var (_, snap) in _files.Values) if (snap != null) snaps.Add(snap);
            _c.Index = InventoryIndex.Build(snaps, _c.InWorld ? _c.Live : null, _c.Server, _c.Character);
            _c.IndexRevision++;
            if (_window != null) _window.NeedsPass = true;
        }
    }

    /// <summary>Lists the files and re-reads the ones that changed, on a worker.</summary>
    private void StartLoad()
    {
        InventoryStore store = _c.Store;
        var known = new Dictionary<string, (InventoryFileStamp Stamp, CharacterSnapshot? Snapshot)>(_files, StringComparer.OrdinalIgnoreCase);
        _c.Loading = _c.Index == null;
        _loadTask = Task.Run(() =>
        {
            var result = new Dictionary<string, (InventoryFileStamp, CharacterSnapshot?)>(StringComparer.OrdinalIgnoreCase);
            int broken = 0;
            foreach (InventoryFileStamp f in store.ListFiles())
            {
                if (known.TryGetValue(f.Path, out var have) && have.Stamp == f)
                {
                    result[f.Path] = (f, have.Snapshot);
                    if (have.Snapshot == null) broken++;
                    continue;
                }
                CharacterSnapshot? s = InventoryStore.Load(f.Path, out _);
                if (s == null) broken++;
                result[f.Path] = (f, s);
            }
            return (result, broken);
        });
    }

    private void RefreshIconSupport()
    {
        _c.IconsAvailable = Host.HasUi && Host.TryGetUiInfo(out UiInfoNative info) && info.OpLevel >= 2;
    }

    /// <summary>A pass when one is wanted, or on the refresh interval while visible (ages tick on).</summary>
    private void Record(UiWindow w, long now, long interval, Action<UiWindow> draw)
    {
        if (_c.Index != null && _view != null && w.Visible && _c.IndexRevision != _lastDrawnRevision)
        {
            _lastDrawnRevision = _c.IndexRevision;
            w.NeedsPass = true;
        }
        bool due = w.NeedsPass || (w.Visible && now >= w.NextPassMs);
        if (!due) return;
        if (!_c.IconsAvailable && Host.HasUi) RefreshIconSupport();
        w.NeedsPass = false;
        w.NextPassMs = now + interval;
        w.Begin();
        try
        {
            draw(w);
        }
        catch (Exception ex)
        {
            w.TextWrapped($"Something went wrong drawing this window: {ex.GetType().Name}: {ex.Message}");
            Host.Log($"[RynthInventory] {w.Key} pass failed: {ex}");
        }
        w.End();
    }

    private int _lastDrawnRevision = -1;
}
