using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthAi.LegacyUi;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.Meta;

internal sealed class MetaManager
{
    private readonly LegacyUiSettings _settings;
    private readonly RynthCoreHost _host;
    private readonly PlayerVitalsCache _vitals;
    private WorldObjectCache? _objectCache;
    private uint _playerId;

    // ── State tracking ───────────────────────────────────────────────────────
    private string _lastState = "";
    private DateTime _stateStartTime = DateTime.Now;

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

    public void SetObjectCache(WorldObjectCache cache)
    {
        _objectCache = cache;
        _expressions?.SetObjectCache(cache);
    }

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

    public ExpressionEngine Expressions => _expressions ??= CreateExpressionEngine();

    private ExpressionEngine CreateExpressionEngine()
    {
        var engine = new ExpressionEngine(_host);
        engine.SetPlayerId(_playerId);
        engine.SetObjectCache(_objectCache);
        return engine;
    }

    public void HandleChat(string text)
    {
        _lastChatText = text;
        _lastChatTime = DateTime.Now;
    }

    public void Think()
    {
        if (!_settings.IsMacroRunning || !_settings.EnableMeta || _settings.MetaRules == null)
            return;

        // ── State change / forced reset ──────────────────────────────────────
        if (_settings.CurrentState != _lastState || _settings.ForceStateReset)
        {
            _lastState = _settings.CurrentState;
            _stateStartTime = DateTime.Now;
            _watchdogActive = false;
            _settings.ForceStateReset = false;
            foreach (var r in _settings.MetaRules) r.HasFired = false;
        }

        double secondsInState = (DateTime.Now - _stateStartTime).TotalSeconds;

        // ── Portal state edge detection ───────────────────────────────────────
        bool currentPortal = _host.HasIsPortaling && _host.IsPortaling();
        _portalEnteredThisTick = currentPortal && !_lastPortalState;
        _portalExitedThisTick  = !currentPortal && _lastPortalState;
        _lastPortalState = currentPortal;

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
                if (rule.Children == null || rule.Children.Count == 0) return false;
                foreach (var child in rule.Children)
                    if (!EvaluateCondition(child, secondsInState)) return false;
                return true;
            }

            case MetaConditionType.Any:
            {
                if (rule.Children == null || rule.Children.Count == 0) return false;
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
                        if (obj.ObjectClass == AcObjectClass.Creature &&
                            _objectCache.Distance(obj.Id, pid) <= maxDist &&
                            rx.IsMatch(obj.Name))
                            matchCount++;
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
                    if (obj.ObjectClass == AcObjectClass.Creature &&
                        _objectCache.Distance(obj.Id, pid) <= maxD)
                        return false;
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

            case MetaConditionType.Expression:
                return !string.IsNullOrEmpty(rule.ConditionData) &&
                       ExpressionEngine.ToBool(Expressions.Evaluate(rule.ConditionData));

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
                    if (_host.HasInvokeChatParser)
                        _host.InvokeChatParser(cmd);
                    else
                        _host.WriteToChat($"[RynthAi] ChatCommand (no parser): {cmd}", 1);
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
                if (string.IsNullOrWhiteSpace(rule.ActionData)) break;
                string routeName = rule.ActionData.Split(';')[0];
                string navFolder = @"C:\Games\RynthSuite\RynthAi\NavProfiles";
                string fullPath = Path.Combine(navFolder, routeName + ".nav");
                if (File.Exists(fullPath))
                {
                    try
                    {
                        _settings.CurrentNavPath = fullPath;
                        _settings.CurrentRoute = NavRouteParser.Load(fullPath);
                        _settings.ActiveNavIndex = _settings.CurrentRoute.RouteType == NavRouteType.Follow
                            ? 0
                            : FindNearestWaypoint(_settings.CurrentRoute);
                        _host.WriteToChat($"[RynthAi Meta] Route \u2192 {routeName}", 1);
                    }
                    catch (Exception ex) { _host.WriteToChat($"[RynthAi Meta] Load Error: {ex.Message}", 1); }
                }
                else { _host.WriteToChat($"[RynthAi Meta] Route missing: {fullPath}", 1); }
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

            case MetaActionType.SetNTOption:
                _host.WriteToChat($"[RynthAi Meta] SetNTOption: {rule.ActionData}", 1);
                break;

            case MetaActionType.ExpressionAction:
                string exprText = ProcessData(rule.ActionData);
                if (!string.IsNullOrEmpty(exprText))
                {
                    if (!Expressions.TryExecuteAction(exprText))
                        _host.WriteToChat($"[RynthAi Expr] Unknown action: {exprText}", 1);
                }
                break;
        }
    }

}
