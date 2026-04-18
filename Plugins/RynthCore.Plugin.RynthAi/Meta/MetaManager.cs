using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed class MetaManager
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;
    private readonly PlayerVitalsCache _vitals;
    private WorldObjectCache? _objectCache;
    private FellowshipTracker? _fellowshipTracker;
    private QuestTracker? _questTracker;
    private BuffManager? _buffManager;
    private uint _playerId;

    /// <summary>
    /// Callback for handling /mt (Mag-Tools) commands that need plugin-level access.
    /// Set by the plugin after construction via <see cref="SetMtCommandHandler"/>.
    /// </summary>
    private Func<string, bool>? _mtCommandHandler;

    // ── State tracking ───────────────────────────────────────────────────────
    private string _lastState = "";
    private DateTime _stateStartTime = DateTime.Now;
    private bool _lastMacroRunning;

    // ── Chat tracking ────────────────────────────────────────────────────────
    private string _lastChatText = "";
    private DateTime _lastChatTime = DateTime.MinValue;
    private Match? _lastChatMatch;

    // ── Call stack (CallMetaState / ReturnFromCall) ──────────────────────────
    private readonly Stack<string> _stateStack = new();

    // ── Watchdog ─────────────────────────────────────────────────────────────
    private bool _watchdogActive;
    private string _watchdogState = "";
    private float _watchdogMetersRequired;
    private double _watchdogSecondsConfig;
    private DateTime _watchdogExpiration;
    private Vector3 _lastWatchdogPos;

    // ── Portal state tracking ────────────────────────────────────────────────
    private bool _lastPortalState;
    private bool _portalEnteredThisTick;
    private bool _portalExitedThisTick;

    // ── Vendor state tracking ─────────────────────────────────────────────────
    private uint _openVendorId;
    private bool _vendorClosedThisTick;

    // ── Enchantment read buffers (reused per tick to avoid allocation) ────────
    private readonly uint[] _enchSpellIds = new uint[256];
    private readonly double[] _enchExpiryTimes = new double[256];

    // ── Expression engine ────────────────────────────────────────────────────
    private ExpressionEngine? _expressions;

    public MetaManager(LegacyUiSettings settings, RynthCoreHost host, PlayerVitalsCache vitals)
    {
        _settings = settings;
        _host = host;
        _vitals = vitals;
    }

    public void SetMtCommandHandler(Func<string, bool> handler) => _mtCommandHandler = handler;

    public void SetObjectCache(WorldObjectCache cache)
    {
        _objectCache = cache;
        _expressions?.SetObjectCache(cache);
    }

    public void SetFellowshipTracker(FellowshipTracker tracker)
    {
        _fellowshipTracker = tracker;
        _expressions?.SetFellowshipTracker(tracker);
    }

    public void SetQuestTracker(QuestTracker tracker)
    {
        _questTracker = tracker;
        _expressions?.SetQuestTracker(tracker);
    }

    public void SetBuffManager(BuffManager buffManager) => _buffManager = buffManager;

    public void SetPlayerId(uint id)
    {
        _playerId = id;
        _expressions?.SetPlayerId(id);
    }

    private int FindNearestWaypoint(NavRouteParser route)
    {
        if (route.Points.Count == 0 || !NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew))
            return 0;

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < route.Points.Count; i++)
        {
            NavPoint point = route.Points[i];
            if (point.Type != NavPointType.Point)
                continue;

            double distance = Math.Sqrt(Math.Pow(point.NS - ns, 2) + Math.Pow(point.EW - ew, 2));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>
    /// Follow and Once routes must run from the first point so opening
    /// Recall/Portal/Chat actions fire. Circular and Linear routes jump
    /// in at the nearest traversable Point.
    /// </summary>
    private int StartIndexForRoute(NavRouteParser route)
    {
        return route.RouteType switch
        {
            NavRouteType.Follow => 0,
            NavRouteType.Once => 0,
            _ => FindNearestWaypoint(route)
        };
    }

    public ExpressionEngine Expressions => _expressions ??= CreateExpressionEngine();

    private ExpressionEngine CreateExpressionEngine()
    {
        var engine = new ExpressionEngine(_host);
        engine.SetPlayerId(_playerId);
        engine.SetObjectCache(_objectCache);
        engine.SetFellowshipTracker(_fellowshipTracker);
        engine.SetQuestTracker(_questTracker);
        engine.SetSettings(_settings);
        return engine;
    }

    public void HandleChat(string text)
    {
        _lastChatText = text;
        _lastChatTime = DateTime.Now;
    }

    public void OnVendorOpen(uint vendorId)
    {
        if (vendorId == 0) return;
        _openVendorId = vendorId;
    }

    public void OnVendorClose(uint vendorId)
    {
        if (vendorId == 0 || vendorId != _openVendorId) return;
        _openVendorId = 0;
        _vendorClosedThisTick = true;
    }

    public void Think()
    {
        if (!_settings.IsMacroRunning || !_settings.EnableMeta || _settings.MetaRules == null)
        {
            _lastMacroRunning = _settings.IsMacroRunning;
            return;
        }

        // Macro just started — force reset so rules re-evaluate even if state
        // name matches where we stopped. Otherwise HasFired stays latched across
        // a stop/start cycle and IF:Always rules like EmbedNav never re-fire.
        if (!_lastMacroRunning)
            _settings.ForceStateReset = true;
        _lastMacroRunning = true;

        // ── State change / forced reset ──────────────────────────────────────
        // Only reset HasFired on ForceStateReset (set by meta actions: SetState,
        // CallState, ReturnFromCall, macro start, meta load, watchdog).
        // Operational state cycling (Combat/Looting/Default/Navigating/Buffing)
        // does NOT set ForceStateReset and must NOT re-fire latched rules —
        // otherwise EmbedNav reloads the nav route from index 0 on every cycle.
        if (_settings.ForceStateReset)
        {
            _stateStartTime = DateTime.Now;
            _watchdogActive = false;
            _settings.ForceStateReset = false;
            foreach (var r in _settings.MetaRules) r.HasFired = false;
        }
        _lastState = _settings.CurrentState;

        double secondsInState = (DateTime.Now - _stateStartTime).TotalSeconds;

        // ── Portal state edge detection ───────────────────────────────────────
        bool currentPortal = _host.HasIsPortaling && _host.IsPortaling();
        _portalEnteredThisTick = currentPortal && !_lastPortalState;
        _portalExitedThisTick  = !currentPortal && _lastPortalState;
        _lastPortalState = currentPortal;

        // ── Vendor edge detection (reset one-shot flag each tick) ─────────────
        _vendorClosedThisTick = false;

        // ── Watchdog ─────────────────────────────────────────────────────────
        if (_watchdogActive &&
            _host.HasGetPlayerPose &&
            _host.TryGetPlayerPose(out _, out float wx, out float wy, out float wz, out _, out _, out _, out _))
        {
            var currentPos = new Vector3(wx, wy, wz);
            float distMoved = Vector3.Distance(currentPos, _lastWatchdogPos);

            if (distMoved < _watchdogMetersRequired)
            {
                if (DateTime.Now > _watchdogExpiration)
                {
                    _watchdogActive = false;
                    _settings.CurrentState = _watchdogState;
                    _settings.ForceStateReset = true;
                    _host.WriteToChat($"[RynthAi] WATCHDOG TRIGGERED \u2192 {_watchdogState}", 1);
                    return;
                }
            }
            else
            {
                _lastWatchdogPos = currentPos;
                _watchdogExpiration = DateTime.Now.AddSeconds(_watchdogSecondsConfig);
            }
        }

        // ── Rule evaluation ───────────────────────────────────────────────────
        foreach (var rule in _settings.MetaRules)
        {
            if (!string.Equals(rule.State, _settings.CurrentState, StringComparison.OrdinalIgnoreCase))
                continue;
            if (rule.HasFired) continue;

            if (EvaluateCondition(rule, secondsInState))
            {
                rule.HasFired = true;
                rule.LastFiredAt = DateTime.Now;

                if (_settings.MetaDebug)
                    _host.WriteToChat($"[Meta] {rule.State} | {DescribeCondition(rule)} → {DescribeAction(rule)}", 1);

                ExecuteAction(rule);

                // If state changed during this action, stop evaluating further rules this tick
                if (_settings.CurrentState != _lastState || _settings.ForceStateReset)
                    break;
            }
        }
    }

    // ── Condition evaluation ─────────────────────────────────────────────────

    private bool EvaluateCondition(MetaRule rule, double secondsInState)
    {
        switch (rule.Condition)
        {
            case MetaConditionType.Always: return true;
            case MetaConditionType.Never:  return false;

            case MetaConditionType.All:
            {
                if (rule.Children == null || rule.Children.Count == 0) return true;
                foreach (var child in rule.Children)
                    if (!EvaluateCondition(child, secondsInState)) return false;
                return true;
            }

            case MetaConditionType.Any:
            {
                if (rule.Children == null || rule.Children.Count == 0) return true;
                foreach (var child in rule.Children)
                    if (EvaluateCondition(child, secondsInState)) return true;
                return false;
            }

            case MetaConditionType.Not:
                if (rule.Children == null || rule.Children.Count == 0) return false;
                return !EvaluateCondition(rule.Children[0], secondsInState);

            // ── Vital checks (value) ──────────────────────────────────────────
            case MetaConditionType.MainHealthLE:
                return int.TryParse(rule.ConditionData, out int hv) && _vitals.CurrentHealth <= (uint)hv;

            case MetaConditionType.MainManaLE:
                return int.TryParse(rule.ConditionData, out int mv) && _vitals.CurrentMana <= (uint)mv;

            case MetaConditionType.MainStamLE:
                return int.TryParse(rule.ConditionData, out int sv) && _vitals.CurrentStamina <= (uint)sv;

            // ── Vital checks (percentage) ─────────────────────────────────────
            case MetaConditionType.MainHealthPHE:
                return int.TryParse(rule.ConditionData, out int hpct) &&
                       _vitals.MaxHealth > 0 &&
                       (_vitals.CurrentHealth * 100.0 / _vitals.MaxHealth) <= hpct;

            case MetaConditionType.MainManaPHE:
                return int.TryParse(rule.ConditionData, out int mpct) &&
                       _vitals.MaxMana > 0 &&
                       (_vitals.CurrentMana * 100.0 / _vitals.MaxMana) <= mpct;

            case MetaConditionType.CharacterDeath:
                return _vitals.CurrentHealth == 0;

            case MetaConditionType.VitaePHE:
            {
                if (!int.TryParse(rule.ConditionData, out int threshold)) return false;
                if (!_host.HasGetVitae || _playerId == 0) return false;
                float v = _host.GetVitae(_playerId);
                int penalty = 100 - (int)Math.Round(v * 100.0f);
                return penalty >= threshold;
            }

            // ── Time ──────────────────────────────────────────────────────────
            case MetaConditionType.SecondsInState_GE:
            case MetaConditionType.SecondsInStateP_GE:
                return double.TryParse(rule.ConditionData, out double reqSecs) && secondsInState >= reqSecs;

            // ── Chat ──────────────────────────────────────────────────────────
            case MetaConditionType.ChatMessage:
            case MetaConditionType.ChatMessageCapture:
            {
                if (string.IsNullOrEmpty(rule.ConditionData)) return false;
                if ((DateTime.Now - _lastChatTime).TotalSeconds > 1.0) return false;
                try
                {
                    var match = Regex.Match(_lastChatText, rule.ConditionData);
                    if (match.Success)
                    {
                        if (rule.Condition == MetaConditionType.ChatMessageCapture)
                            _lastChatMatch = match;
                        return true;
                    }
                }
                catch { }
                return false;
            }

            // ── Pack slots ────────────────────────────────────────────────────
            case MetaConditionType.PackSlots_LE:
            {
                if (!int.TryParse(rule.ConditionData, out int targetSlots) || _objectCache == null) return false;
                int used = 0;
                foreach (var item in _objectCache.GetDirectInventory())
                    if (item.WieldedLocation <= 0) used++;
                return (102 - used) <= targetSlots;
            }

            // ── Inventory item count ──────────────────────────────────────────
            case MetaConditionType.InventoryItemCount_LE:
            case MetaConditionType.InventoryItemCount_GE:
            {
                if (string.IsNullOrEmpty(rule.ConditionData) || _objectCache == null) return false;
                var parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int targetCount)) return false;
                string itemName = parts[0].Trim();
                int count = 0;
                foreach (var item in _objectCache.GetDirectInventory())
                    if (item.Name.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                        count++;
                return rule.Condition == MetaConditionType.InventoryItemCount_LE
                    ? count <= targetCount
                    : count >= targetCount;
            }

            // ── Spell timer ───────────────────────────────────────────────────
            case MetaConditionType.TimeLeftOnSpell_GE:
            case MetaConditionType.TimeLeftOnSpell_LE:
            {
                if (string.IsNullOrEmpty(rule.ConditionData)) return false;
                var parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 ||
                    !uint.TryParse(parts[0], out uint spellId) ||
                    !double.TryParse(parts[1], out double reqSeconds)) return false;
                if (!_host.HasReadPlayerEnchantments) return false;

                int count = _host.ReadPlayerEnchantments(_enchSpellIds, _enchExpiryTimes, 256);
                if (count <= 0)
                    return rule.Condition == MetaConditionType.TimeLeftOnSpell_LE;

                double serverTime = _host.HasGetServerTime ? _host.GetServerTime() : 0;
                for (int i = 0; i < count; i++)
                {
                    if (_enchSpellIds[i] == spellId)
                    {
                        double remaining = _enchExpiryTimes[i] - serverTime;
                        return rule.Condition == MetaConditionType.TimeLeftOnSpell_GE
                            ? remaining >= reqSeconds
                            : remaining <= reqSeconds;
                    }
                }
                // Spell not found = 0 time remaining
                return rule.Condition == MetaConditionType.TimeLeftOnSpell_LE;
            }

            // ── Monster count within distance ─────────────────────────────────
            case MetaConditionType.MonsterNameCountWithinDistance:
            {
                if (string.IsNullOrEmpty(rule.ConditionData) || _objectCache == null || _playerId == 0) return false;
                var parts = rule.ConditionData.Split(',');
                if (parts.Length < 3 ||
                    !double.TryParse(parts[1], out double maxDist) ||
                    !int.TryParse(parts[2], out int minCount)) return false;
                try
                {
                    var rx = new Regex(parts[0], RegexOptions.IgnoreCase);
                    int matchCount = 0;
                    int pid = unchecked((int)_playerId);
                    foreach (var obj in _objectCache.GetLandscape())
                    {
                        if (obj.Id == pid) continue;
                        if (obj.ObjectClass != AcObjectClass.Monster) continue;
                        float hp = _objectCache.GetHealthRatio(obj.Id);
                        if (hp == 0f || hp < 0f) continue;
                        if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(unchecked((uint)obj.Id))) continue;
                        if (_objectCache.Distance(obj.Id, pid) <= maxDist && rx.IsMatch(obj.Name))
                            matchCount++;
                    }
                    return matchCount >= minCount;
                }
                catch { return false; }
            }

            // ── No monsters within distance ───────────────────────────────────
            case MetaConditionType.NoMonstersWithinDistance:
            {
                if (_objectCache == null || _playerId == 0) return true;
                double.TryParse(rule.ConditionData, out double maxD);
                if (maxD <= 0) maxD = 20.0;
                int pid = unchecked((int)_playerId);
                foreach (var obj in _objectCache.GetLandscape())
                {
                    if (obj.Id == pid) continue; // never count self
                    if (obj.ObjectClass != AcObjectClass.Monster) continue;
                    float hp = _objectCache.GetHealthRatio(obj.Id);
                    if (hp == 0f) continue; // dead, pending reclassification
                    if (hp < 0f) continue;  // never received a health update — stale/untracked
                    if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(unchecked((uint)obj.Id))) continue;
                    if (_objectCache.Distance(obj.Id, pid) <= maxD)
                        return false;
                }
                return true;
            }

            // ── Nav route empty ───────────────────────────────────────────────
            case MetaConditionType.NavrouteEmpty:
                if (_settings.CurrentRoute == null || _settings.CurrentRoute.Points.Count == 0) return true;
                return _settings.ActiveNavIndex >= _settings.CurrentRoute.Points.Count;

            // ── Landblock / landcell ──────────────────────────────────────────
            case MetaConditionType.Landblock_EQ:
            {
                if (string.IsNullOrEmpty(rule.ConditionData) || !_host.HasGetPlayerPose) return false;
                if (!_host.TryGetPlayerPose(out uint cellId, out _, out _, out _, out _, out _, out _, out _)) return false;
                uint landblock = cellId >> 16;
                if (uint.TryParse(rule.ConditionData, System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                    return landblock == hex;
                if (uint.TryParse(rule.ConditionData, out uint dec))
                    return landblock == dec;
                return false;
            }

            case MetaConditionType.Landcell_EQ:
            {
                if (string.IsNullOrEmpty(rule.ConditionData) || !_host.HasGetPlayerPose) return false;
                if (!_host.TryGetPlayerPose(out uint cellId, out _, out _, out _, out _, out _, out _, out _)) return false;
                if (uint.TryParse(rule.ConditionData, System.Globalization.NumberStyles.HexNumber, null, out uint hex))
                    return cellId == hex;
                if (uint.TryParse(rule.ConditionData, out uint dec))
                    return cellId == dec;
                return false;
            }

            case MetaConditionType.PortalspaceEntered:
                return _portalEnteredThisTick;

            case MetaConditionType.PortalspaceExited:
                return _portalExitedThisTick;

            case MetaConditionType.AnyVendorOpen:
                return _openVendorId != 0;

            case MetaConditionType.VendorClosed:
                return _vendorClosedThisTick;

            case MetaConditionType.Expression:
                return !string.IsNullOrEmpty(rule.ConditionData) &&
                       ExpressionEngine.ToBool(Expressions.Evaluate(rule.ConditionData));

            // ── Burden percentage ─────────────────────────────────────────────
            case MetaConditionType.BurdenPercentage_GE:
            {
                if (!int.TryParse(rule.ConditionData, out int threshold)) return false;
                if (!_host.HasGetObjectIntProperty || _playerId == 0) return false;
                if (!_host.TryGetObjectIntProperty(_playerId, 5u, out int encumbVal)) return false;
                if (!_host.TryGetObjectIntProperty(_playerId, 96u, out int encumbCap) || encumbCap <= 0) return false;
                return (encumbVal * 100 / encumbCap) >= threshold;
            }

            // ── Distance to nearest nav route point ───────────────────────────
            case MetaConditionType.DistAnyRoutePT_GE:
            {
                if (!double.TryParse(rule.ConditionData, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out double threshold)) return false;
                if (_settings.CurrentRoute == null || _settings.CurrentRoute.Points.Count == 0) return false;
                if (!NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew)) return false;

                double minDist = double.MaxValue;
                foreach (var pt in _settings.CurrentRoute.Points)
                {
                    if (pt.Type != NavPointType.Point) continue;
                    double d = Math.Sqrt((pt.NS - ns) * (pt.NS - ns) + (pt.EW - ew) * (pt.EW - ew));
                    if (d < minDist) minDist = d;
                }
                return minDist != double.MaxValue && minDist >= threshold;
            }

            // ── Priority monster count within distance ────────────────────────
            case MetaConditionType.MonsterPriorityCountWithinDistance:
            {
                if (_objectCache == null || _playerId == 0) return false;
                var parts = rule.ConditionData.Split(',');
                if (parts.Length < 2 ||
                    !int.TryParse(parts[0], out int minCount) ||
                    !double.TryParse(parts[1], out double maxDist)) return false;
                int pid = unchecked((int)_playerId);
                int matchCount = 0;
                foreach (var obj in _objectCache.GetLandscape())
                {
                    if (obj.ObjectClass != AcObjectClass.Monster) continue;
                    if (_objectCache.GetHealthRatio(obj.Id) == 0f) continue;
                    if (_host.HasObjectIsAttackable && !_host.ObjectIsAttackable(unchecked((uint)obj.Id))) continue;
                    if (_objectCache.Distance(obj.Id, pid) > maxDist) continue;
                    foreach (var mr in _settings.MonsterRules)
                    {
                        if (mr.Name.Equals("Default", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            if (Regex.IsMatch(obj.Name, mr.Name, RegexOptions.IgnoreCase))
                            { matchCount++; break; }
                        }
                        catch { }
                    }
                }
                return matchCount >= minCount;
            }

            // ── Need to buff ──────────────────────────────────────────────────
            case MetaConditionType.NeedToBuff:
                return _buffManager != null && _buffManager.NeedsAnyBuff();

            default: return false;
        }
    }

    // ── Action execution ─────────────────────────────────────────────────────

    private void ExecuteAction(MetaRule rule)
    {
        string ProcessData(string raw)
        {
            if (string.IsNullOrEmpty(raw) || _lastChatMatch == null || !_lastChatMatch.Success) return raw ?? "";
            string result = raw;
            for (int i = 0; i < _lastChatMatch.Groups.Count; i++)
                result = result.Replace($"{{{i}}}", _lastChatMatch.Groups[i].Value);
            return result;
        }

        switch (rule.Action)
        {
            case MetaActionType.All:
                if (rule.ActionChildren != null && rule.ActionChildren.Count > 0)
                    foreach (var child in rule.ActionChildren) ExecuteAction(child);
                else if (rule.Children != null)
                    foreach (var child in rule.Children) ExecuteAction(child);
                break;

            case MetaActionType.ChatCommand:
                string cmd = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(cmd))
                {
                    if (!TryHandleVtCommand(cmd) && !(_mtCommandHandler?.Invoke(cmd) == true))
                    {
                        if (_host.HasInvokeChatParser)
                            _host.InvokeChatParser(cmd);
                        else
                            _host.WriteToChat($"[RynthAi] ChatCommand (no parser): {cmd}", 1);
                    }
                }
                break;

            case MetaActionType.SetMetaState:
                string nextState = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(nextState))
                {
                    _settings.CurrentState = nextState;
                    _settings.ForceStateReset = true;
                }
                break;

            case MetaActionType.CallMetaState:
                string callState = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(callState))
                {
                    _stateStack.Push(_settings.CurrentState);
                    _settings.CurrentState = callState;
                    _settings.ForceStateReset = true;
                }
                break;

            case MetaActionType.ReturnFromCall:
                if (_stateStack.Count > 0)
                {
                    _settings.CurrentState = _stateStack.Pop();
                    _settings.ForceStateReset = true;
                }
                break;

            case MetaActionType.EmbeddedNavRoute:
            {
                if (string.IsNullOrWhiteSpace(rule.ActionData))
                {
                    _host.Log("[Meta] EmbedNav: empty ActionData, ignored");
                    break;
                }
                string routeName = rule.ActionData.Split(';')[0];
                int priorPoints = _settings.CurrentRoute?.Points?.Count ?? 0;

                try
                {
                    // Embedded nav routes live in memory alongside the loaded meta.
                    // If the direct key misses, try normalizing legacy names
                    // like "nav0__MatronHive1_nav" → "MatronHive1".
                    if (!_settings.EmbeddedNavs.ContainsKey(routeName))
                    {
                        string norm = System.Text.RegularExpressions.Regex.Replace(
                            routeName, @"^nav\d+_+", "");
                        if (norm.EndsWith("_nav", StringComparison.OrdinalIgnoreCase))
                            norm = norm.Substring(0, norm.Length - 4);
                        norm = norm.Replace('_', ' ').Trim();
                        foreach (var key in _settings.EmbeddedNavs.Keys)
                        {
                            if (string.Equals(key, norm, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(key.Replace(".nav", "", StringComparison.OrdinalIgnoreCase), norm, StringComparison.OrdinalIgnoreCase))
                            {
                                routeName = key;
                                break;
                            }
                        }
                    }

                    if (_settings.EmbeddedNavs.TryGetValue(routeName, out var embedded))
                    {
                        var newRoute = NavRouteParser.LoadFromLines(embedded);
                        _settings.CurrentNavPath   = $"<embedded:{routeName}>";
                        _settings.CurrentRoute     = newRoute;
                        _settings.ActiveNavIndex   = StartIndexForRoute(newRoute);
                        _settings.EnableNavigation = true;
                        _host.Log($"[Meta] EmbedNav: loaded '{routeName}' ({newRoute.Points.Count} pts, was {priorPoints})");
                        _host.WriteToChat($"[RynthAi Meta] Route \u2192 {routeName} ({newRoute.Points.Count} pts)", 1);
                        break;
                    }

                    // Fallback: a standalone .nav file in NavProfiles.
                    string navFolder = @"C:\Games\RynthSuite\RynthAi\NavProfiles";
                    string fullPath = Path.Combine(navFolder, routeName + ".nav");
                    if (File.Exists(fullPath))
                    {
                        var newRoute = NavRouteParser.Load(fullPath);
                        _settings.CurrentNavPath   = fullPath;
                        _settings.CurrentRoute     = newRoute;
                        _settings.ActiveNavIndex   = StartIndexForRoute(newRoute);
                        _settings.EnableNavigation = true;
                        _host.Log($"[Meta] EmbedNav: loaded file '{routeName}' ({newRoute.Points.Count} pts, was {priorPoints})");
                        _host.WriteToChat($"[RynthAi Meta] Route \u2192 {routeName} ({newRoute.Points.Count} pts)", 1);
                    }
                    else
                    {
                        string keys = string.Join(", ", _settings.EmbeddedNavs.Keys);
                        _host.Log($"[Meta] EmbedNav: route '{routeName}' not found. Embedded keys: [{keys}]");
                        _host.WriteToChat($"[RynthAi Meta] Route missing: {routeName}", 1);
                    }
                }
                catch (Exception ex)
                {
                    _host.Log($"[Meta] EmbedNav error: {ex.Message}");
                    _host.WriteToChat($"[RynthAi Meta] Load Error: {ex.Message}", 1);
                }
                break;
            }

            case MetaActionType.SetWatchdog:
            {
                if (_watchdogActive) break;
                var parts = (rule.ActionData ?? "").Split(';');
                if (parts.Length < 3) break;
                _watchdogState = parts[0].Trim();
                if (!float.TryParse(parts[1].Trim(), out _watchdogMetersRequired)) _watchdogMetersRequired = 5f;
                if (!double.TryParse(parts[2].Trim(), out _watchdogSecondsConfig)) _watchdogSecondsConfig = 5.0;
                _watchdogExpiration = DateTime.Now.AddSeconds(_watchdogSecondsConfig);
                if (_host.HasGetPlayerPose &&
                    _host.TryGetPlayerPose(out _, out float wx, out float wy, out float wz, out _, out _, out _, out _))
                    _lastWatchdogPos = new Vector3(wx, wy, wz);
                _watchdogActive = true;
                break;
            }

            case MetaActionType.ClearWatchdog:
                _watchdogActive = false;
                break;

            case MetaActionType.SetRAOption:
            {
                string optData = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(optData))
                {
                    var parts = optData.Split(';');
                    if (parts.Length >= 2)
                        Expressions.SetOption(parts[0].Trim(), parts[1].Trim());
                }
                break;
            }

            case MetaActionType.GetRAOption:
            {
                string optData = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(optData))
                {
                    var parts = optData.Split(';');
                    if (parts.Length >= 2)
                    {
                        string varName = parts[0].Trim();
                        string optName = parts[1].Trim();
                        Expressions.SetVariable(varName, Expressions.GetOption(optName));
                    }
                }
                break;
            }

            case MetaActionType.ChatExpression:
            {
                string exprSrc = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(exprSrc))
                {
                    string msg = Expressions.Evaluate(exprSrc);
                    if (!string.IsNullOrEmpty(msg))
                    {
                        if (!TryHandleVtCommand(msg) && !(_mtCommandHandler?.Invoke(msg) == true))
                        {
                            if (_host.HasInvokeChatParser)
                                _host.InvokeChatParser(msg);
                            else
                                _host.WriteToChat($"[RynthAi] ChatExpression (no parser): {msg}", 1);
                        }
                    }
                }
                break;
            }

            case MetaActionType.ExpressionAction:
                string exprText = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(exprText))
                {
                    if (!Expressions.TryExecuteAction(exprText))
                        _host.WriteToChat($"[RynthAi Expr] Unknown action: {exprText}", 1);
                }
                break;

            // ── View actions (VTank UI construct — no-op in RynthAi) ──────────
            case MetaActionType.CreateView:
            case MetaActionType.DestroyView:
            case MetaActionType.DestroyAllViews:
                break;
        }
    }

    // ── VTank command translation ───────────────────────────────────────────

    /// <summary>
    /// Intercepts /vt commands from VTank metas and translates them to
    /// equivalent RynthAi settings changes. Returns true if handled.
    /// </summary>
    private bool TryHandleVtCommand(string cmd)
    {
        if (!cmd.StartsWith("/vt ", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;

        string sub = parts[1].ToLower();

        // /vt opt set <option> <value>
        if (sub == "opt" && parts.Length >= 5 &&
            parts[2].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            string optName = parts[3].ToLower();
            string optVal = parts[4];
            return TrySetVtOption(optName, optVal);
        }

        // /vt meta load <name> — load a meta file from MetaFiles folder
        if (sub == "meta" && parts.Length >= 4 &&
            parts[2].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            string name = string.Join(" ", parts, 3, parts.Length - 3);
            string metaDir = @"C:\Games\RynthSuite\RynthAi\MetaFiles";
            string afPath = Path.Combine(metaDir, name + ".af");
            string metPath = Path.Combine(metaDir, name + ".met");

            string loadPath = File.Exists(afPath) ? afPath : File.Exists(metPath) ? metPath : "";
            if (!string.IsNullOrEmpty(loadPath))
            {
                try
                {
                    LoadedMeta loaded = loadPath.EndsWith(".met", StringComparison.OrdinalIgnoreCase)
                        ? MetFileParser.Load(loadPath)
                        : AfFileParser.Load(loadPath);
                    if (loaded.Rules.Count > 0)
                    {
                        _settings.MetaRules = loaded.Rules;
                        _settings.EmbeddedNavs.Clear();
                        foreach (var kvp in loaded.EmbeddedNavs)
                            _settings.EmbeddedNavs[kvp.Key] = kvp.Value;
                        _settings.CurrentState = loaded.Rules[0].State;
                        _settings.ForceStateReset = true;
                        _settings.CurrentMetaPath = loadPath;
                        _host.WriteToChat($"[RynthAi] Loaded meta: {name}", 1);
                    }
                }
                catch { }
            }
            else
            {
                _host.WriteToChat($"[RynthAi] Meta not found: {name}", 1);
            }
            return true;
        }

        // /vt nav load <name> — load a nav route
        if (sub == "nav" && parts.Length >= 4 &&
            parts[2].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            string name = string.Join(" ", parts, 3, parts.Length - 3);
            _settings.CurrentNavPath = name;
            _host.WriteToChat($"[RynthAi] Nav route set: {name}", 1);
            return true;
        }

        // /vt loot load <name> — load a loot profile
        if ((sub == "loot" || sub == "lootprofile") && parts.Length >= 4 &&
            parts[2].Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            string name = string.Join(" ", parts, 3, parts.Length - 3);
            _settings.CurrentLootPath = name;
            _host.WriteToChat($"[RynthAi] Loot profile set: {name}", 1);
            return true;
        }

        // /vt settings load / loadchar — ignore silently
        if (sub == "settings") return true;

        return false;
    }

    // VTank option name → RynthAi settings mapping
    private static readonly Dictionary<string, string> VtOptionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enablecombat"]              = "EnableCombat",
        ["enablelooting"]             = "EnableLooting",
        ["enablenav"]                 = "EnableNavigation",
        ["enablebuffing"]             = "EnableBuffing",
        ["enablemeta"]                = "EnableMeta",
        ["opendoors"]                 = "OpenDoors",
        ["idlepeacemode"]             = "PeaceModeWhenIdle",
        ["idlebufftopoff"]            = "RebuffWhenIdle",
        ["summonpets"]                = "SummonPets",
        ["combinesalvage"]            = "EnableCombineSalvage",
        ["attackdistance"]            = "MonsterRange",
        ["approachdistance"]          = "ApproachRange",
        ["navpriorityboost"]          = "BoostNavPriority",
        ["lootpriorityboost"]         = "BoostLootPriority",
        ["dooropenrange"]             = "OpenDoorRange",
        ["autofellowmanagement"]      = "AutoFellowMgmt",
        ["switchwandstodebuff"]       = "UseDispelItems",
        ["lootonlyrarecorpses"]       = "MineOnly",
    };

    private bool TrySetVtOption(string vtName, string vtValue)
    {
        // Translate VTank bool strings to numeric
        string value = vtValue.ToLower() switch
        {
            "true"  => "1",
            "false" => "0",
            "on"    => "1",
            "off"   => "0",
            _       => vtValue
        };

        if (VtOptionMap.TryGetValue(vtName, out string? raName))
        {
            var map = Expressions.BuildSettingsMapPublic();
            if (map.TryGetValue(raName, out var entry))
            {
                entry.Set(value);
                return true;
            }
        }

        // Not mapped — store as a generic option so expressions can still read it
        Expressions.SetOption(vtName, value);
        return true;
    }

    // ── Debug helpers ─────────────────────────────────────────────────────────

    private static string DescribeCondition(MetaRule rule)
    {
        string cond = rule.Condition.ToString();
        if (!string.IsNullOrEmpty(rule.ConditionData))
            cond += $"({Truncate(rule.ConditionData, 40)})";
        return cond;
    }

    private static string DescribeAction(MetaRule rule)
    {
        string act = rule.Action.ToString();
        if (!string.IsNullOrEmpty(rule.ActionData))
            act += $"({Truncate(rule.ActionData, 60)})";
        else if (rule.Action == MetaActionType.All)
        {
            int count = (rule.ActionChildren?.Count ?? 0) + (rule.Children?.Count ?? 0);
            act += $"({count} sub-actions)";
        }
        return act;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

}
