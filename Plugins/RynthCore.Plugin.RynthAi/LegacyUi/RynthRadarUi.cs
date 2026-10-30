using System;
using RynthCore.Loot;
using RynthCore.Plugin.RynthAi.Raycasting;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthAi.LegacyUi;

/// <summary>
/// The radar's data source: BuildSnapshotJson (RynthPluginGetRadarSnapshot) feeds the
/// engine's Radar and Dungeon Map faces - walls, floors and visited cells from
/// DungeonMapUi's rasterised cache, markers from the object cache, the player pose.
/// Visited-cell tracking advances on each snapshot. This used to be a plugin-drawn
/// radar window as well; nothing here draws now.
/// </summary>
internal sealed class RynthRadarUi
{
    private readonly RynthCoreHost _host;
    private readonly LegacyUiSettings _settings;
    private DungeonMapUi? _mapData;
    private WorldObjectCache? _objectCache;

    // Per-session explored state (grid cells at the DungeonMapUi rasteriser's
    // resolution). Cleared whenever the player transitions landblocks, so
    // re-entering a dungeon resets the coloring.
    private const float GridCell = 0.5f;
    private readonly System.Collections.Generic.HashSet<(int gx, int gy)> _visitedCells = new();
    private uint _visitedLandblock;
    // Visited cells merged into strips — rebuilt only when _visitedCells grows.
    private System.Collections.Generic.Dictionary<float, System.Collections.Generic.List<(float x0, float y0, float x1, float y1)>>? _visitedFillStrips;
    private int _lastVisitedCount = -1;

    // Radar kind per static world object (portal 2, door 3, 0 = not drawn) with its label,
    // decided once per object and name instead of on every 30 Hz snapshot (item-type read,
    // five name searches, portal label tokenising for each of hundreds of dungeon objects).
    // Static objects (ids below 0x80000000) don't move or change type. Only settled answers
    // are kept: an object whose item type isn't readable yet is asked again next snapshot.
    // Cleared with the visited cells on a landblock change. (2026-10-02 flood work.)
    private readonly System.Collections.Generic.Dictionary<int, (string Name, byte Kind, string Label)> _staticKinds = new();
    private int _creatureNpcLandblock;
    // Radar kind per creature (monster, NPC, player, vendor, your pet), settled answers only,
    // per landblock. Fellows are players looked up live (membership changes).
    private readonly System.Collections.Generic.Dictionary<int, (string Name, byte Kind)> _creatureKinds = new();
    // Dynamic (0x8...) non-creature objects: summoned portal, lifestone or item on the ground.
    private readonly System.Collections.Generic.Dictionary<int, (string Name, byte Kind)> _dynamicKinds = new();
    // Corpse killer from its LongDesc ("Killed by X."), "" until the corpse is identified; asked
    // again at most once a second per corpse until then.
    private readonly System.Collections.Generic.Dictionary<int, (string Name, string Killer, long NextCheckMs)> _corpseKillers = new();
    // Creatures already drawn this snapshot (the landscape pass skips them).
    private readonly System.Collections.Generic.HashSet<int> _drawnCreatures = new();
    private string _playerName = string.Empty;
    private long _playerNameAt;

    // Marker kinds: RadarKind in the engine (RynthCore.Engine, UI/Data/RadarSnapshotJson.cs).
    // 0-3 are the original four; the rest were appended 2026-10-04 (older engines drop them).
    internal const byte KindMonster = 0, KindNpc = 1, KindPortal = 2, KindDoor = 3, KindPlayer = 4,
        KindFellow = 5, KindPet = 6, KindVendor = 7, KindCorpse = 8, KindOwnCorpse = 9,
        KindLifestone = 10, KindGroundItem = 11;

    // PublicWeenieDesc._bitfield flags (as ExpressionEngine.ResolveEffectiveObjectClass).
    private const uint BfPlayer = 0x8, BfAttackable = 0x10, BfVendor = 0x200;
    private const uint TypeCreature = 0x10u, TypePortal = 0x10000u, TypeLifeStone = 0x10000000u;
    private const uint STypeLongDesc = 16u;

    /// <summary>Fellowship membership by character id (FellowshipTracker.IsMember); null = no fellows.</summary>
    internal Func<int, bool>? IsFellowId { get; set; }
    /// <summary>Fellowship membership by name, for a corpse's killer; null = no fellows.</summary>
    internal Func<string, bool>? IsFellowName { get; set; }

    public RynthRadarUi(RynthCoreHost host, LegacyUiSettings settings)
    {
        _host = host;
        _settings = settings;
    }

    internal void SetMapData(DungeonMapUi mapData) => _mapData = mapData;
    public void SetWorldObjectCache(WorldObjectCache cache) => _objectCache = cache;

    // ── Visited strip cache ─────────────────────────────────────────────

    private void RebuildRadarVisitedStrips()
    {
        if (_mapData?._floorCells is null || _visitedCells.Count == 0 || _mapData._zLayers is null)
        {
            _visitedFillStrips = null;
            return;
        }

        var result = new System.Collections.Generic.Dictionary<float, System.Collections.Generic.List<(float, float, float, float)>>(
            _mapData._zLayers.Count);
        foreach (var (layerZ, cells) in _mapData._floorCells)
        {
            var flatVisited = new System.Collections.Generic.List<(int, int)>();
            foreach (var ((gx, gy), ctype) in cells)
                if (ctype == DungeonMapUi.CellType.Flat && _visitedCells.Contains((gx, gy)))
                    flatVisited.Add((gx, gy));
            if (flatVisited.Count == 0) continue;
            var strips = DungeonMapUi.BuildVisitedStripsFrom(flatVisited);
            if (strips.Count > 0) result[layerZ] = strips;
        }
        _visitedFillStrips = result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Best-effort portal display name. STypePortalDest (38) holds the raw destination
    /// when available, otherwise we fall back to the object name. The label is then
    /// reduced to just the destination proper:
    ///   • first line only (strip anything past a newline)
    ///   • tokenise on whitespace/punctuation
    ///   • drop any token that is literally "portal"
    ///   • drop any token that looks like a coord ("5.6N", "-12.3W", etc.)
    /// Result is trimmed and ellipsis-truncated at 22 chars.
    /// </summary>
    private string GetPortalLabel(WorldObject wo)
    {
        const uint STypePortalDest = 38u;
        string label = wo.Name ?? string.Empty;

        if (_host.HasGetObjectStringProperty
            && _host.TryGetObjectStringProperty((uint)wo.Id, STypePortalDest, out string dest)
            && !string.IsNullOrEmpty(dest))
        {
            label = dest;
        }

        int nl = label.IndexOf('\n');
        if (nl >= 0) label = label.Substring(0, nl);

        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < label.Length)
        {
            while (i < label.Length && IsLabelSep(label[i])) i++;
            if (i >= label.Length) break;
            int start = i;
            while (i < label.Length && !IsLabelSep(label[i])) i++;
            string tok = label.Substring(start, i - start);

            if (tok.Equals("portal", StringComparison.OrdinalIgnoreCase)) continue;
            if (tok.Equals("to",     StringComparison.OrdinalIgnoreCase)) continue;
            if (LooksLikeCoord(tok)) continue;
            if (IsNumericToken(tok))  continue;

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(tok);
        }

        string result = sb.ToString().Trim();
        if (result.Length > 22) result = result.Substring(0, 22) + "…";
        return result;
    }

    private static bool IsLabelSep(char c)
        => c == ' ' || c == '\t' || c == ',' || c == ';' || c == ':' || c == '(' || c == ')' || c == '.';

    /// <summary>True if the token looks like a coordinate — a digit/sign followed
    /// eventually by a compass letter (N/S/E/W), e.g. "5.6N", "-12.3W", "0N".
    /// Note: IsLabelSep consumes '.' so coord fragments arrive as "56N" / "123W".</summary>
    /// <summary>True if every char in the token is a digit or sign (e.g. "49", "-12",
    /// "+5"). Catches bare coord numbers that don't carry a compass letter.</summary>
    private static bool IsNumericToken(string tok)
    {
        if (tok.Length == 0) return false;
        int start = 0;
        if (tok[0] == '-' || tok[0] == '+') { if (tok.Length == 1) return false; start = 1; }
        for (int i = start; i < tok.Length; i++)
            if (!char.IsDigit(tok[i])) return false;
        return true;
    }

    private static bool LooksLikeCoord(string tok)
    {
        if (tok.Length < 2) return false;
        char last = tok[tok.Length - 1];
        bool endsCompass = last == 'N' || last == 'S' || last == 'E' || last == 'W'
                        || last == 'n' || last == 's' || last == 'e' || last == 'w';
        if (!endsCompass) return false;
        char first = tok[0];
        if (!(char.IsDigit(first) || first == '-' || first == '+')) return false;
        for (int i = 1; i < tok.Length - 1; i++)
            if (!char.IsDigit(tok[i])) return false;
        return true;
    }

    private static bool IsDoorName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.Contains("Door",       StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gate",       StringComparison.OrdinalIgnoreCase)
            || name.Contains("Portcullis", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Hatch",      StringComparison.OrdinalIgnoreCase)
            || name.Contains("Trapdoor",   StringComparison.OrdinalIgnoreCase);
    }

    // ── Snapshot bridge for the engine's radar and dungeon map faces ────────
    // Visited-cell tracking advances here, so the visited overlay fills in
    // while a radar or map face is polling.

    /// <summary>
    /// Build a JSON snapshot for the Avalonia radar. <paramref name="engineKnownMapVersion"/>
    /// is the MapVersion the engine currently has cached; pass 0 on first call.
    /// When it matches the live landblock, walls/fills are omitted to keep the
    /// payload small.
    /// </summary>
    internal string BuildSnapshotJson(uint engineKnownMapVersion)
    {
        try
        {
            var payload = BuildSnapshotPayload(engineKnownMapVersion);
            return System.Text.Json.JsonSerializer.Serialize(
                payload, RadarSnapshotJsonContext.Default.RadarSnapshotPayload);
        }
        catch
        {
            return "{}";
        }
    }

    private RadarSnapshotPayload BuildSnapshotPayload(uint engineKnownMapVersion)
    {
        var payload = new RadarSnapshotPayload();
        if (!_host.HasGetPlayerPose) return payload;
        if (!_host.TryGetPlayerPose(out uint cellId, out float px, out float py, out float pz,
                out _, out _, out _, out _))
            return payload;

        uint landblock = cellId >> 16;
        bool isIndoor = (cellId & 0xFFFF) >= 0x100 && landblock != 0;
        float gxLB = ((landblock >> 8) & 0xFF) * 192f;
        float gyLB = (landblock & 0xFF) * 192f;
        float playerWX = px + gxLB;
        float playerWY = py + gyLB;

        // Reset visited state on landblock change — matches Render() exactly.
        if (landblock != _visitedLandblock)
        {
            _visitedLandblock = landblock;
            _visitedCells.Clear();
            _visitedFillStrips = null;
            _lastVisitedCount = -1;
            _staticKinds.Clear();
            _dynamicKinds.Clear();
            _corpseKillers.Clear();
        }

        if (isIndoor)
        {
            int pgx = (int)MathF.Floor(playerWX / GridCell);
            int pgy = (int)MathF.Floor(playerWY / GridCell);
            int r = Math.Clamp(_settings.RadarWallPaintRadius, 0, 40);
            for (int oy = -r; oy <= r; oy++)
                for (int ox = -r; ox <= r; ox++)
                    _visitedCells.Add((pgx + ox, pgy + oy));
        }

        if (_visitedCells.Count != _lastVisitedCount && _mapData != null)
        {
            _lastVisitedCount = _visitedCells.Count;
            RebuildRadarVisitedStrips();
        }

        _host.TryGetPlayerHeading(out float heading);

        // Outdoor landblocks share geometry-less snapshots — MapVersion=landblock
        // still works, but GeometryIncluded stays false because there's no wall data.
        bool sameVersion = engineKnownMapVersion != 0 && engineKnownMapVersion == landblock;

        payload.MapVersion = landblock;
        payload.GeometryIncluded = !sameVersion && isIndoor;
        payload.IsIndoor = isIndoor;
        payload.Player = new RadarPlayer
        {
            CellId = cellId,
            Landblock = landblock,
            X = px, Y = py, Z = pz,
            WorldX = playerWX, WorldY = playerWY,
            Heading = heading,
        };

        // NS/EW only meaningful outdoors; we send always so the panel can
        // decide whether to display them. NaN signals "unavailable".
        if (NavCoordinateHelper.TryGetNavCoords(_host, out double ns, out double ew))
        {
            payload.Ns = ns;
            payload.Ew = ew;
        }

        if (isIndoor && _mapData != null)
        {
            _mapData.EnsureCache(landblock);
            var zLayers = _mapData._zLayers;
            if (zLayers is { Count: > 0 })
            {
                payload.LayerZs.Capacity = zLayers.Count;
                foreach (var z in zLayers) payload.LayerZs.Add(z);
                int curIdx = _mapData.BestLayerIdxFor(pz);
                payload.CurrentLayerZ = zLayers[curIdx];

                if (payload.GeometryIncluded)
                {
                    if (_mapData._outerEdgesRaw != null)
                    {
                        foreach (var kv in _mapData._outerEdgesRaw)
                        {
                            var edges = kv.Value;
                            var arr = new float[edges.Count * 6];
                            for (int i = 0; i < edges.Count; i++)
                            {
                                var (ax, ay, bx, by, _, gx, gy) = edges[i];
                                int o = i * 6;
                                arr[o] = ax; arr[o + 1] = ay;
                                arr[o + 2] = bx; arr[o + 3] = by;
                                arr[o + 4] = gx; arr[o + 5] = gy;
                            }
                            payload.Walls.Add(new RadarWallLayer { Z = kv.Key, Segments = arr });
                        }
                    }
                    if (_mapData._fillStrips != null)
                    {
                        foreach (var kv in _mapData._fillStrips)
                        {
                            var strips = kv.Value;
                            var arr = new float[strips.Count * 5];
                            for (int i = 0; i < strips.Count; i++)
                            {
                                var (x0, y0, x1, y1, type) = strips[i];
                                int o = i * 5;
                                arr[o] = x0; arr[o + 1] = y0;
                                arr[o + 2] = x1; arr[o + 3] = y1;
                                arr[o + 4] = (float)(int)type;
                            }
                            payload.Fills.Add(new RadarFillLayer { Z = kv.Key, Strips = arr });
                        }
                    }
                }

                if (_visitedFillStrips != null)
                {
                    foreach (var kv in _visitedFillStrips)
                    {
                        var strips = kv.Value;
                        var arr = new float[strips.Count * 4];
                        for (int i = 0; i < strips.Count; i++)
                        {
                            var (x0, y0, x1, y1) = strips[i];
                            int o = i * 4;
                            arr[o] = x0; arr[o + 1] = y0;
                            arr[o + 2] = x1; arr[o + 3] = y1;
                        }
                        payload.Visited.Add(new RadarVisitedLayer { Z = kv.Key, Strips = arr });
                    }
                }
            }
        }

        if (_objectCache != null && _host.HasGetObjectPosition && landblock != 0)
            AddMarkers(payload, landblock, isIndoor);

        return payload;
    }

    // ── Markers ─────────────────────────────────────────────────────────────
    // Every kind is sent; the engine filters by its per-kind settings. Each object's kind is
    // decided once and kept (per object id and name) wherever the answer is settled, because
    // this runs on every radar poll (10-30 Hz) over every creature and world object nearby.

    /// <summary>
    /// Where an object's landblock puts it in world space. Outdoors every landblock counts: the client has the
    /// neighbours loaded and an object's position is local to its own landblock, so it is offset by that one
    /// (2026-10-15: objects over the border used to show only once you crossed it). In a dungeon only the
    /// player's own landblock counts (the neighbours' cells are other places).
    /// </summary>
    private static bool TryLandblockOffset(uint cell, uint playerLandblock, bool indoor, out float gx, out float gy)
    {
        uint lb = cell >> 16;
        gx = ((lb >> 8) & 0xFF) * 192f;
        gy = (lb & 0xFF) * 192f;
        return lb != 0 && (!indoor || lb == playerLandblock);
    }

    private void AddMarkers(RadarSnapshotPayload payload, uint landblock, bool indoor)
    {
        var cache = _objectCache!;
        if (_creatureNpcLandblock != (int)landblock)
        {
            _creatureNpcLandblock = (int)landblock;
            _creatureKinds.Clear();
        }

        string me = PlayerName();
        string petPrefix = me.Length > 0 ? me + "'s " : string.Empty;
        string myCorpse = me.Length > 0 ? "Corpse of " + me : string.Empty;
        long now = Environment.TickCount64;
        _drawnCreatures.Clear();

        foreach (var wo in cache.GetLandscape())
        {
            if (!_host.TryGetObjectPosition((uint)wo.Id, out uint cCellId,
                    out float cox, out float coy, out float coz)) continue;
            if (!TryLandblockOffset(cCellId, landblock, indoor, out float cgx, out float cgy)) continue;
            _drawnCreatures.Add(wo.Id);
            AddCreature(payload, wo, petPrefix, cox + cgx, coy + cgy, coz);
        }

        foreach (var wo in cache.GetLandscapeObjects())
        {
            uint uid = (uint)wo.Id;
            string woName = wo.Name ?? string.Empty;

            // Creatures outside the cache's creature set: players and NPCs its rescue pass
            // promoted are filed by class only (Monster-class ones are in the set).
            if (wo.ObjectClass is AcObjectClass.Monster or AcObjectClass.Player or AcObjectClass.Npc
                    or AcObjectClass.Vendor or AcObjectClass.CombatPet)
            {
                if (wo.ObjectClass == AcObjectClass.Monster || _drawnCreatures.Contains(wo.Id)) continue;
                if (!_host.TryGetObjectPosition(uid, out uint cc, out float cx, out float cy, out float cz)) continue;
                if (!TryLandblockOffset(cc, landblock, indoor, out float rgx, out float rgy)) continue;
                AddCreature(payload, wo, petPrefix, cx + rgx, cy + rgy, cz);
                continue;
            }

            if (wo.ObjectClass == AcObjectClass.Corpse)
            {
                if (!_host.TryGetObjectPosition(uid, out uint kc, out float kx, out float ky, out float kz)) continue;
                if (!TryLandblockOffset(kc, landblock, indoor, out float kgx, out float kgy)) continue;
                bool own = IsOwnCorpse(wo, woName, me, myCorpse, now);
                payload.Markers.Add(new RadarMarker
                {
                    Kind = own ? KindOwnCorpse : KindCorpse,
                    X = kx + kgx, Y = ky + kgy, Z = kz,
                    Label = woName,
                    Id = uid,
                });
                continue;
            }

            if (uid >= 0x80000000u)
            {
                AddDynamicObject(payload, wo, woName, landblock, indoor);
                continue;
            }

            // Settled answer for this object and name: skip the reads and the name work.
            if (_staticKinds.TryGetValue(wo.Id, out var known) && known.Name == woName && known.Kind == 0)
                continue;

            if (!_host.TryGetObjectPosition(uid, out uint oCellId,
                    out float oox, out float ooy, out float ooz)) continue;
            if (!TryLandblockOffset(oCellId, landblock, indoor, out float ogx, out float ogy)) continue;

            byte kind;
            string label;
            if (known.Name == woName && known.Kind != 0 && _staticKinds.ContainsKey(wo.Id))
            {
                kind = known.Kind;
                // A portal's label can come from a property that arrives later: build it
                // each time (portals are few). A door's or lifestone's label is its name.
                label = kind == KindPortal ? GetPortalLabel(wo) : known.Label;
            }
            else
            {
                uint typeFlags = 0;
                bool typeRead = _host.HasGetItemType && _host.TryGetItemType(uid, out typeFlags);
                bool isPortal = typeRead && (typeFlags & TypePortal) != 0;
                bool isLifestone = !isPortal && typeRead && (typeFlags & TypeLifeStone) != 0;
                bool isDoor = !isPortal && !isLifestone && IsDoorName(woName);
                kind = isPortal ? KindPortal : isLifestone ? KindLifestone : isDoor ? KindDoor : (byte)0;
                label = isPortal ? GetPortalLabel(wo) : kind != 0 ? woName : string.Empty;
                // Keep it once the item type was readable (portal or not is then known).
                if (typeRead) _staticKinds[wo.Id] = (woName, kind, label);
                if (kind == 0) continue;
            }

            payload.Markers.Add(new RadarMarker
            {
                Kind = kind,
                X = oox + ogx, Y = ooy + ogy, Z = ooz,
                Label = label,
                Id = uid,
            });
        }

        // Corpses come and go within one landblock: forget them past a few hundred (re-read is cheap).
        if (_corpseKillers.Count > 512) _corpseKillers.Clear();
    }

    private void AddCreature(RadarSnapshotPayload payload, WorldObject wo, string petPrefix, float x, float y, float z)
    {
        string name = wo.Name ?? string.Empty;
        byte kind;
        if (_creatureKinds.TryGetValue(wo.Id, out var known) && known.Name == name)
            kind = known.Kind;
        else
        {
            kind = ClassifyCreature(wo, name, petPrefix, out bool settled);
            if (settled) _creatureKinds[wo.Id] = (name, kind);
        }
        // Fellowship membership changes while you stand there: asked live (a cached id lookup).
        if (kind == KindPlayer && IsFellowId?.Invoke(wo.Id) == true)
            kind = KindFellow;
        payload.Markers.Add(new RadarMarker
        {
            Kind = kind,
            X = x, Y = y, Z = z,
            Label = wo.Name, // Dungeon Map labels; the Radar's hover tooltip
            Id = (uint)wo.Id,
        });
    }

    /// <summary>
    /// Monster, NPC, player, vendor or your pet. Your pet: the server names a summon
    /// "&lt;you&gt;'s &lt;pet&gt;" (PetManager detects it the same way). Otherwise the weenie
    /// bitfield: player, vendor, attackable (monster) or not (NPC). A zero bitfield isn't
    /// trusted (the engine answers 0 when it can't read the object): then the cache's class or
    /// the attackable check answers for now and the bitfield is asked again next poll.
    /// (This replaces a CreatureType == 14 "NPC" test: 14 is Undead, so undead showed as NPCs.)
    /// </summary>
    private byte ClassifyCreature(WorldObject wo, string name, string petPrefix, out bool settled)
    {
        settled = true;
        if (petPrefix.Length > 0 && name.StartsWith(petPrefix, StringComparison.Ordinal))
            return KindPet;
        uint uid = (uint)wo.Id;
        if (_host.HasGetObjectBitfield && _host.TryGetObjectBitfield(uid, out uint bf) && bf != 0)
        {
            if ((bf & BfPlayer) != 0) return KindPlayer;
            if ((bf & BfVendor) != 0) return KindVendor;
            return (bf & BfAttackable) != 0 ? KindMonster : KindNpc;
        }
        settled = false;
        switch (wo.ObjectClass)
        {
            case AcObjectClass.Player: return KindPlayer;
            case AcObjectClass.Vendor: return KindVendor;
            case AcObjectClass.Npc:    return KindNpc;
        }
        if (_host.HasObjectIsAttackable)
            return _host.ObjectIsAttackable(uid) ? KindMonster : KindNpc;
        return KindMonster;
    }

    /// <summary>A summoned portal, a lifestone or an item on the ground; other dynamic objects aren't drawn.</summary>
    private void AddDynamicObject(RadarSnapshotPayload payload, WorldObject wo, string woName, uint landblock, bool indoor)
    {
        byte kind;
        if (_dynamicKinds.TryGetValue(wo.Id, out var known) && known.Name == woName)
            kind = known.Kind;
        else
        {
            uint typeFlags = 0;
            bool typeRead = _host.HasGetItemType && _host.TryGetItemType((uint)wo.Id, out typeFlags);
            if (wo.ObjectClass == AcObjectClass.Portal || (typeRead && (typeFlags & TypePortal) != 0))
                kind = KindPortal;
            else if (!typeRead || typeFlags == 0 || (typeFlags & TypeCreature) != 0)
                return; // not known yet, or a creature on its way into the creature set: ask again
            else if ((typeFlags & TypeLifeStone) != 0)
                kind = KindLifestone;
            else
                kind = KindGroundItem;
            _dynamicKinds[wo.Id] = (woName, kind);
        }
        if (!_host.TryGetObjectPosition((uint)wo.Id, out uint cell, out float x, out float y, out float z)) return;
        if (!TryLandblockOffset(cell, landblock, indoor, out float dgx, out float dgy)) return;
        payload.Markers.Add(new RadarMarker
        {
            Kind = kind,
            X = x + dgx, Y = y + dgy, Z = z,
            Label = kind == KindPortal ? GetPortalLabel(wo) : woName,
            Id = (uint)wo.Id,
        });
    }

    /// <summary>
    /// A corpse you may loot: your own ("Corpse of &lt;you&gt;"), or one whose LongDesc says it was
    /// killed by you or a fellow. LongDesc only arrives once the corpse is identified (looting or
    /// selecting it requests that; the radar never does), so until then it counts as someone else's.
    /// </summary>
    private bool IsOwnCorpse(WorldObject wo, string name, string me, string myCorpse, long now)
    {
        if (myCorpse.Length > 0 && name.Equals(myCorpse, StringComparison.OrdinalIgnoreCase)) return true;
        if (!_corpseKillers.TryGetValue(wo.Id, out var ck) || ck.Name != name)
            ck = (name, string.Empty, 0);
        if (ck.Killer.Length == 0 && now >= ck.NextCheckMs)
        {
            string killer = string.Empty;
            if (_host.HasGetObjectStringProperty
                && _host.TryGetObjectStringProperty((uint)wo.Id, STypeLongDesc, out string longDesc))
                killer = ExtractKiller(longDesc);
            ck = (name, killer, now + 1000);
            _corpseKillers[wo.Id] = ck;
        }
        if (ck.Killer.Length == 0) return false;
        if (me.Length > 0 && NormalizeCharName(ck.Killer).Equals(NormalizeCharName(me), StringComparison.OrdinalIgnoreCase))
            return true;
        return IsFellowName?.Invoke(ck.Killer) == true;
    }

    /// <summary>"Killed by X." in a corpse's LongDesc: X, or "" (read as CorpseOpenController reads it).</summary>
    internal static string ExtractKiller(string? longDesc)
        => RynthAiPlugin.ExtractCorpseKillerName(longDesc) ?? string.Empty;

    /// <summary>Without GM sigils (+, @, #): the server leaves them out of "Killed by X".</summary>
    private static string NormalizeCharName(string name)
    {
        string t = name.Trim();
        while (t.Length > 0 && (t[0] == '+' || t[0] == '@' || t[0] == '#')) t = t.Substring(1);
        return t.Trim();
    }

    private string PlayerName()
    {
        long now = Environment.TickCount64;
        if (_playerName.Length > 0 && now - _playerNameAt < 30_000) return _playerName;
        uint pid = _host.HasGetPlayerId ? _host.GetPlayerId() : 0;
        if (pid != 0 && _host.TryGetObjectName(pid, out string n) && !string.IsNullOrEmpty(n))
        {
            _playerName = n;
            _playerNameAt = now;
        }
        return _playerName;
    }
}
