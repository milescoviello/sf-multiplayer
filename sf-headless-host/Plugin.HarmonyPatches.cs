using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFHeadlessHost
{
    public partial class Plugin
    {
        internal static bool InjectInputPrefix(object __instance)
        {
            try
            {
                _prefixCallCount++;
                if ((object)__instance == null) return true;
                var ctrlComp = __instance as Component;
                if ((object)ctrlComp == null) return true;
                var rig = ctrlComp.gameObject;

                int slot = -1;
                foreach (var kv in SlotToRig)
                {
                    if (kv.Value == rig) { slot = kv.Key; break; }
                }
                if (slot < 0)
                {
                    if (_prefixCallCount % 600 == 1)
                        Log.LogDebug($"InjectInputPrefix: rig {rig.name} not ours (prefix call #{_prefixCallCount})");
                    return true;
                }
                _prefixOurRigCount++;

                if (!SlotInputs.TryGetValue(slot, out var input)) return true;

                bool verbose = (_prefixOurRigCount == 1 || _prefixOurRigCount % 120 == 0);
                if (verbose)
                    Log.LogInfo($"[INSTR2a] Prefix entry: slot={slot} SlotInputs.stick=({input.StickX:0.00},{input.StickY:0.00}) ourCallCount={_prefixOurRigCount}");

                var actionsField = AccessTools.Field(__instance.GetType(), "mPlayerActions");
                if ((object)actionsField == null) return true;
                var actions = actionsField.GetValue(__instance);
                if ((object)actions == null) return true;

                // Read CURRENT Movement.X/Y BEFORE our write — tells us whether
                // InControl rebuilt it since our last write last frame.
                if (verbose)
                {
                    var preX = ReadAxis(actions, "Movement", "X");
                    var preY = ReadAxis(actions, "Movement", "Y");
                    Log.LogInfo($"[INSTR2b] PRE-write Movement=({preX:0.00},{preY:0.00})");
                }

                // Stuff our values into Movement.X / .Y backing fields and
                // Movement.thisValue. Read by the original Update body
                // immediately after this prefix returns.
                ForceTwoAxis(actions, "Movement", input.StickX, input.StickY);
                ForceTwoAxis(actions, "Aiming",   input.AimX,   input.AimY);

                // Read again AFTER write — confirms write took effect synchronously.
                if (verbose)
                {
                    var postX = ReadAxis(actions, "Movement", "X");
                    var postY = ReadAxis(actions, "Movement", "Y");
                    // Probe Controller early-return gate variables.
                    var ctrlT = __instance.GetType();
                    var inactiveF = AccessTools.Field(ctrlT, "inactive");
                    var infoF    = AccessTools.Field(ctrlT, "info");
                    var hasCtrlF = AccessTools.Field(ctrlT, "mHasControl");
                    bool inactive = (object)inactiveF != null && (bool)inactiveF.GetValue(__instance);
                    bool hasCtrl  = (object)hasCtrlF != null && (bool)hasCtrlF.GetValue(__instance);
                    bool isDead = false;
                    if ((object)infoF != null) {
                        var infoVal = infoF.GetValue(__instance);
                        if ((object)infoVal != null) {
                            var deadF = AccessTools.Field(infoVal.GetType(), "isDead");
                            if ((object)deadF != null) isDead = (bool)deadF.GetValue(infoVal);
                        }
                    }
                    // GameManager.inFight + stillInMenu — the prime suspects.
                    var gmT = CachedType("GameManager");
                    bool inFight = false, stillInMenu = false;
                    if ((object)gmT != null) {
                        var fF = AccessTools.Field(gmT, "inFight");
                        var mF = AccessTools.Field(gmT, "stillInMenu");
                        if ((object)fF != null) inFight = (bool)fF.GetValue(null);
                        if ((object)mF != null) stillInMenu = (bool)mF.GetValue(null);
                    }
                    bool willEarlyReturn = inactive || isDead || (!inFight && !stillInMenu);
                    Log.LogInfo($"[INSTR2c] POST-write Movement=({postX:0.00},{postY:0.00}) inactive={inactive} hasControl={hasCtrl} isDead={isDead} inFight={inFight} stillInMenu={stillInMenu} willEarlyReturn={willEarlyReturn}");
                }

                // Button-action backing field updates. PlayerAction is a
                // OneAxisInputControl with private InputControlState
                // (lastState, thisState, nextState). The IsPressed/WasPressed
                // accessors read thisState. We reach into thisState's .State
                // bool and .Value float to set the press.
                ForceButton(actions, "Jump",        (input.Buttons & 0x01) != 0);
                ForceButton(actions, "Jump2",       (input.Buttons & 0x01) != 0);
                ForceButton(actions, "PunchOrFire", (input.Buttons & 0x02) != 0);
                ForceButton(actions, "Block",       (input.Buttons & 0x04) != 0);
                ForceButton(actions, "Throw",       (input.Buttons & 0x08) != 0);
            }
            catch (Exception e)
            {
                if (Verbose && Log != null) Log.LogDebug($"InjectInputPrefix: {e.Message}");
            }
            return true; // always let original Update run
        }

        // [INSTR3] MoveRight/MoveLeft entry-logger prefixes removed — they ran on
        // every frame a player moved and only logged; their diagnostic job is done.

        // Phase 6.5 Step 1 — IsServer=true. Static property getter so no __instance.
        internal static void IsServerPostfix(ref bool __result)
        {
            __result = true;
        }

        // Phase 6.5 Step 2d — IsNetworkMatch=true. Pin against Controller's reset.
        internal static void IsNetworkMatchPostfix(ref bool __result)
        {
            __result = true;
        }
        internal static void InitSyncedObjectsPostfix()
        {
            _initSyncedCallCount++;
            Log.LogInfo($"[P6.9 init] InitSyncedObjects called (#{_initSyncedCallCount}). PrepareMapForTravel reached settle-end on the oracle.");
        }
        internal static void InitMapDataObjectsPostfix()
        {
            _initMapDataCallCount++;
            Log.LogInfo($"[P6.9 init] InitMapDataObjects called (#{_initMapDataCallCount}).");
        }
        internal static void ReadyUpPostfix()
        {
            _readyUpCallCount++;
            Log.LogInfo($"[P6.9 init] MultiplayerManager.ReadyUp called (#{_readyUpCallCount}).");
        }
        internal static bool SetNetworkMatchPrefix(ref bool v)
        {
            _setNetMatchInterceptCount++;
            if (!v && _setNetMatchInterceptCount <= 5)
                Log.LogInfo($"[P6.5] SetNetworkMatch(false) intercepted #{_setNetMatchInterceptCount} → forcing true");
            v = true;
            return true; // run original with forced arg
        }
        // Prefix on GameManager.SpawnRandomWeapon: the stock timer is disabled on
        // the server (TickOracleCombatTimers is the one spawner), so this only
        // parks the stock counter. Two spawners used to run at once.
        internal static bool SpawnRandomWeaponPrefix(object __instance)
        {
            try
            {
                var rwcField = AccessTools.Field(__instance.GetType(), "randomWeaponCounter");
                if ((object)rwcField != null) rwcField.SetValue(__instance, 1000f);
            }
            catch { }
            return false;
        }

        // One stock weapon drop (GameManager.SpawnRandomWeapon): height 11 x map
        // scale, Z = +/-Random(0,8) alternating sides every drop (carried across
        // rounds; the lobby's first drop goes to the positive side), presents on
        // Xmas maps (Landfall scenes 104-124).
        private static bool _spawnedLastWeaponOnLeftSide;

        // Bridge "dropWeapon" (test tool): weapon <id> (the drop id, as in the
        // "[weapons] drop" log) falls from 4 units above player <slot>.
        internal string DropWeaponAbove(int slot, int id)
        {
            Vector3 p;
            if (!_reportedPos.TryGetValue(slot, out p)) return "no position for slot " + slot;
            var mmType = CachedType("MultiplayerManager");
            var mm = (object)mmType != null ? UnityEngine.Object.FindObjectOfType(mmType) : null;
            var spawnWeapon = (object)mm != null ? AccessTools.Method(mmType, "SpawnWeapon", new[] { typeof(int), typeof(Vector3), typeof(bool) }) : null;
            if ((object)spawnWeapon == null) return "SpawnWeapon not found";
            Vector3 at = new Vector3(0f, p.y + 4f, p.z);
            spawnWeapon.Invoke(mm, new object[] { id, at, false });
            return "dropped weapon " + id + " at " + at.ToString("0.0");
        }
        internal static void ServerSpawnWeapon(object gmInst)
        {
            try
            {
                _srwCallCount++;
                int weaponIdx = PickWeaponId(_srwCallCount);
                if (weaponIdx < 0) return;
                var gmType = gmInst.GetType();
                float zOff = UnityEngine.Random.Range(0f, 8f);
                if (_spawnedLastWeaponOnLeftSide) zOff *= -1f;
                _spawnedLastWeaponOnLeftSide = !_spawnedLastWeaponOnLeftSide;
                float scale = 1f;
                var lastAppliedScaleF = AccessTools.Field(gmType, "LastAppliedScale");
                if ((object)lastAppliedScaleF != null && lastAppliedScaleF.GetValue(gmInst) is float f) scale = f;
                Vector3 spawnPos = Vector3.up * (11f * scale) + Vector3.forward * zOff;
                int scene = (object)Instance != null ? Instance._currentSceneIndex : 0;
                bool present = scene >= 104 && scene <= 124;

                var mmType = CachedType("MultiplayerManager");
                if ((object)mmType == null) return;
                var mmInst = UnityEngine.Object.FindObjectOfType(mmType);
                if ((object)mmInst == null)
                {
                    var all = Resources.FindObjectsOfTypeAll(mmType);
                    if (all != null && all.Length > 0) mmInst = all[0];
                }
                var spawnWeapon = (object)mmInst != null ? AccessTools.Method(mmType, "SpawnWeapon", new[] { typeof(int), typeof(Vector3), typeof(bool) }) : null;
                if ((object)spawnWeapon == null)
                {
                    if (_srwCallCount <= 3) Log.LogWarning("[weapons] MultiplayerManager.SpawnWeapon unavailable; skipping drop.");
                    return;
                }
                if (_srwCallCount <= 5 || _srwCallCount % 10 == 0)
                    Log.LogInfo($"[weapons] drop #{_srwCallCount}: id={weaponIdx} pos={spawnPos} present={present}");
                spawnWeapon.Invoke(mmInst, new object[] { weaponIdx, spawnPos, present });
            }
            catch (Exception e) { Log.LogWarning($"[weapons] drop threw: {e.Message}"); }
        }

        // Snakes (snake gun + snake maps). Stock SnakeAI picks a target only on
        // the host, from ControllerHandler.players, and broadcasts it with
        // NetworkSyncableObject.NewSnakeTarget; every client then chases with its
        // own copy and the bite lands on the victim's own client. The server's
        // player rigs aren't in ControllerHandler.players, so server snakes never
        // picked anyone: no target was ever sent and snakes never did damage.
        // This prefix does the host's targeting over the server rigs. It leaves
        // mTargetController unset, so the server copy never bites (stock: only
        // the victim's client applies a bite).
        private static System.Reflection.FieldInfo _snTarget, _snMainRig, _snRange, _snInfo, _snLife, _snNso, _ciIsDead;
        private static System.Reflection.MethodInfo _nsoNewSnakeTarget;
        private static System.Reflection.FieldInfo _nsoIsSnakeF;
        private static Type _hipType, _charInfoType;
        private static bool _snakeReflectOk, _snakeReflectTried;
        private static System.Reflection.FieldInfo _nsoLastSnakeSendF;
        private static int _snakeTargetsSent;
        internal static bool SkipOnServerPrefix() => false;

        internal static void SnakeAIUpdatePrefix(object __instance)
        {
            try
            {
                if (!_snakeReflectTried)
                {
                    _snakeReflectTried = true;
                    var t = __instance.GetType();
                    _snTarget = AccessTools.Field(t, "target");
                    _snMainRig = AccessTools.Field(t, "mainRig");
                    _snRange = AccessTools.Field(t, "targetRange");
                    _snInfo = AccessTools.Field(t, "info");
                    _snLife = AccessTools.Field(t, "lifeCounter");
                    _snNso = AccessTools.Field(t, "mNetworkSyncableObject");
                    _charInfoType = CachedType("CharacterInformation");
                    _hipType = CachedType("Hip");
                    _ciIsDead = (object)_charInfoType != null ? AccessTools.Field(_charInfoType, "isDead") : null;
                    var nsoType = CachedType("NetworkSyncableObject");
                    _nsoNewSnakeTarget = (object)nsoType != null ? AccessTools.Method(nsoType, "NewSnakeTarget") : null;
                    _snakeReflectOk = (object)_snTarget != null && (object)_snMainRig != null && (object)_snRange != null
                        && (object)_snLife != null && (object)_hipType != null && (object)_ciIsDead != null;
                    if (!_snakeReflectOk) Log.LogWarning("[snake] SnakeAI reflection incomplete — server snake targeting off.");
                }
                if (!_snakeReflectOk || (object)Instance == null) return;
                // Stock NetworkSyncableObject.Awake flags a snake (mIsSnake) so it
                // never sends position updates: a snake's only message is its
                // target. This build's Awake doesn't, so the server's snakes sent
                // positions and every client read a position byte as the target
                // (255 = nobody): snakes never chased, bit or pushed anything.
                var nsoObj = (object)_snNso != null ? _snNso.GetValue(__instance) : null;
                if (nsoObj != null)
                {
                    if ((object)_nsoIsSnakeF == null) _nsoIsSnakeF = AccessTools.Field(nsoObj.GetType(), "mIsSnake");
                    if ((object)_nsoIsSnakeF != null && !(bool)_nsoIsSnakeF.GetValue(nsoObj)) _nsoIsSnakeF.SetValue(nsoObj, true);
                }
                if ((_snTarget.GetValue(__instance) as Rigidbody) != null) return;      // already chasing
                if ((float)_snLife.GetValue(__instance) < 0.5f) return;
                var info = (object)_snInfo != null ? _snInfo.GetValue(__instance) : null;
                if (info != null && (bool)_ciIsDead.GetValue(info)) return;
                var main = _snMainRig.GetValue(__instance) as Rigidbody;
                if (main == null) return;
                float range = (float)_snRange.GetValue(__instance);
                int best = -1; Rigidbody bestHip = null; float bestD = 100f;   // stock search radius cap
                foreach (var kv in SlotToRig)
                {
                    var rig = kv.Value;
                    if (rig == null) continue;
                    var ci = (object)_charInfoType != null ? rig.GetComponent(_charInfoType) : null;
                    if (ci != null && (bool)_ciIsDead.GetValue(ci)) continue;
                    var hip = rig.GetComponentInChildren(_hipType);
                    if (hip == null) continue;
                    float d = Vector3.Distance(main.position, hip.transform.position);
                    if (d < bestD && d < range) { bestD = d; best = kv.Key; bestHip = hip.GetComponent<Rigidbody>(); }
                }
                if (best < 0) return;
                var nso = (object)_snNso != null ? _snNso.GetValue(__instance) : null;
                // Stock sends a snake's target at most every 0.5 s and clears it
                // every second (sending 255); the re-pick goes out when the 0.5 s
                // window reopens, and the snake waits target-less until then. We
                // used to pick inside the window: the send was skipped but the
                // server's snake chased anyway, so every client only ever heard
                // "no target" after the first second and its snake stopped.
                if (nso != null)
                {
                    if ((object)_nsoLastSnakeSendF == null) _nsoLastSnakeSendF = AccessTools.Field(nso.GetType(), "mLastSnakeSendTime");
                    if ((object)_nsoLastSnakeSendF != null && Time.time - (float)_nsoLastSnakeSendF.GetValue(nso) < 0.5f) return;
                }
                if (nso != null && (object)_nsoNewSnakeTarget != null)
                {
                    _nsoNewSnakeTarget.Invoke(nso, new object[] { (byte)best });   // broadcasts to clients (rate-limited 0.5s by stock)
                    if (_snakeTargetsSent++ < 5 || _snakeTargetsSent % 50 == 0)
                        Log.LogInfo($"[snake] target slot {best} ({bestD:0.0}u) #{_snakeTargetsSent}");
                }
                _snTarget.SetValue(__instance, bestHip);   // server copy chases too (NewSnakeTarget cleared it: server PlayerControllers is empty)
            }
            catch (Exception e) { if (_snakeTargetsSent++ < 3) Log.LogWarning($"[snake] {e.Message}"); }
        }

        internal static bool GetRandomWeaponIndexPrefix(bool mustBeActive, ref GameObject weaponObject, ref int __result)
        {
            _grwiCallCount++;
            weaponObject = null;
            __result = PickWeaponId(_grwiCallCount);
            if (_grwiCallCount <= 3 || _grwiCallCount % 5 == 0)
                Log.LogInfo($"[P6.5] GetRandomWeaponIndexPrefix call#{_grwiCallCount} → returning {__result}");
            return false; // skip original
        }
        // Stock weapon drops (WeaponSelectionHandler.GetRandomWeaponIndex): a
        // rarity-weighted bag — every active weapon goes in Weapon.Rarity times,
        // draws are random WITHOUT replacement, and the bag refills when empty.
        // The stock handler builds its list from the weapon-select menu UI,
        // which a headless server never builds (the stock call returns -1), so
        // we build the same bag from the character prefab's Weapons children
        // (named "<index> <Name>"), the same source the UI reads. The /weapons
        // allow-list (comp rules) filters the bag. The old fallback here was
        // `seed % 8`: only weapons 0-7, in a fixed, predictable order.
        private static readonly List<int> _weaponBag = new List<int>();
        private static List<KeyValuePair<int, int>> _weaponTable;   // (weapon index, rarity)
        private static readonly System.Random _weaponRng = new System.Random();

        private static List<KeyValuePair<int, int>> GetWeaponTable()
        {
            if (_weaponTable != null) return _weaponTable;
            var table = ReadWeaponTableFromSelectionHandler();
            string source = "weapon-select list";
            if (table.Count == 0) { table = ReadWeaponTableFromPrefab(); source = "character prefab (fallback)"; }
            if (table.Count > 0)
            {
                _weaponTable = table;
                int total = 0; foreach (var kv in table) total += kv.Value;
                Log.LogInfo($"[weapons] drop table from {source}: {table.Count} weapons, bag size {total} (rarity-weighted, like stock).");
            }
            return table;
        }

        // The stock source: WeaponSelectionHandler.m_Weapons (one SingleWeaponUI
        // per selectable weapon) filtered by GetActiveStateForWeapon (weapon and
        // its category switched on). Clients resolve a spawned id through the
        // same list (GetWeaponByIndex → m_WeaponObjects), so only these ids exist
        // on every client. The handler does initialize on the headless server
        // (its Unity log shows "generated WeaponRarity").
        private static List<KeyValuePair<int, int>> ReadWeaponTableFromSelectionHandler()
        {
            var table = new List<KeyValuePair<int, int>>();
            try
            {
                var wshType = CachedType("WeaponSelectionHandler");
                var swType = CachedType("SingleWeaponUI");
                if ((object)wshType == null || (object)swType == null) return table;
                UnityEngine.Object handler = UnityEngine.Object.FindObjectOfType(wshType);
                if (handler == null)
                {
                    var all = Resources.FindObjectsOfTypeAll(wshType);
                    if (all != null && all.Length > 0) handler = all[0];
                }
                if (handler == null) return table;
                var list = AccessTools.Field(wshType, "m_Weapons")?.GetValue(handler) as System.Collections.IList;
                var isActive = AccessTools.Method(wshType, "GetActiveStateForWeapon");
                var idxF = AccessTools.Field(swType, "WeaponIndex");
                var rarF = AccessTools.Field(swType, "Rarity");
                var nameF = AccessTools.Field(swType, "WeaponName");
                var catF = AccessTools.Field(swType, "Category");
                if (list == null || (object)idxF == null || (object)rarF == null) return table;
                var wanted = ParseNameList("SF_WEAPONS");   // comp pool; unset = stock active set
                var log = new System.Text.StringBuilder();
                foreach (var w in list)
                {
                    if (w == null) continue;
                    bool active = true;
                    try { if ((object)isActive != null) active = (bool)isActive.Invoke(handler, new[] { w }); } catch { }
                    string name = (object)nameF != null ? nameF.GetValue(w) as string : null;
                    string cat = (object)catF != null ? catF.GetValue(w) as string : null;
                    int idx = Convert.ToInt32(idxF.GetValue(w));
                    int rarity = Convert.ToInt32(rarF.GetValue(w));
                    log.Append($" {cat}:{name}={idx}/r{rarity}{(active ? "" : "(off)")}");
                    bool use = wanted != null ? NameListContains(wanted, cat, name) : active;
                    if (use && rarity > 0) table.Add(new KeyValuePair<int, int>(idx, rarity));
                }
                Log.LogInfo($"[weapons] stock weapon list:{log}");
                if (wanted != null) Log.LogInfo($"[weapons] SF_WEAPONS filter: {table.Count} of {list.Count} weapons allowed.");
            }
            catch (Exception e) { Log.LogWarning($"[weapons] weapon-select list read failed: {e.Message}"); }
            return table;
        }

        private static List<KeyValuePair<int, int>> ReadWeaponTableFromPrefab()
        {
            var table = new List<KeyValuePair<int, int>>();
            try
            {
                var weapons = (object)_cachedPlayerPrefab != null ? _cachedPlayerPrefab.transform.Find("Weapons") : null;
                var weaponType = CachedType("Weapon");
                if ((object)weapons != null && (object)weaponType != null)
                {
                    var rarityP = AccessTools.Property(weaponType, "Rarity");
                    var dropF = AccessTools.Field(weaponType, "weaponDrop");
                    for (int i = 0; i < weapons.childCount; i++)
                    {
                        var child = weapons.GetChild(i);
                        int idx;
                        if (!int.TryParse(child.name.Split(' ')[0], out idx) || idx < 0 || idx > 255) continue;
                        var w = child.GetComponent(weaponType);
                        if ((object)w == null) continue;
                        int rarity = (object)rarityP != null ? Convert.ToInt32(rarityP.GetValue(w, null)) : 1;
                        if (rarity <= 0) continue;
                        if ((object)dropF != null && (dropF.GetValue(w) as UnityEngine.Object) == null) continue;
                        table.Add(new KeyValuePair<int, int>(idx, rarity));
                    }
                }
            }
            catch (Exception e) { Log.LogWarning($"[weapons] prefab table build failed: {e.Message}"); }
            return table;
        }

        // Call when the allow-list changes so the next draw uses the new filter.
        internal static void ResetWeaponBag() => _weaponBag.Clear();

        private static void RefillWeaponBag()
        {
            _weaponBag.Clear();
            foreach (var kv in GetWeaponTable())
            {
                if (_allowedWeaponIds.Count > 0 && !_allowedWeaponIds.Contains(kv.Key)) continue;
                for (int r = 0; r < kv.Value; r++) _weaponBag.Add(kv.Key);
            }
            // An explicit admin allow-list wins even for ids the table lacks.
            if (_weaponBag.Count == 0 && _allowedWeaponIds.Count > 0)
                foreach (var id in _allowedWeaponIds) _weaponBag.Add(id);
        }

        internal static int PickWeaponId(int seed)
        {
            if (_weaponBag.Count == 0) RefillWeaponBag();
            if (_weaponBag.Count == 0) return seed % 8;   // prefab not cached yet: last resort
            int i = _weaponRng.Next(_weaponBag.Count);
            int pick = _weaponBag[i];
            _weaponBag.RemoveAt(i);
            return pick;
        }
        internal static bool SendBroadcastPrefix(object[] __args)
        {
            try
            {
                _p65BroadcastCount++;
                var data = __args.Length > 0 ? __args[0] as byte[] : null;
                // (byte)__args[1] on a boxed byte-backed enum may throw on
                // strict CLRs; Convert.ToInt32 unboxes via IConvertible and
                // works for either a raw byte or an enum.
                byte msgType = UnboxByte(__args.Length > 1 ? __args[1] : null);
                bool ignoreServer = __args.Length > 2 && __args[2] is bool b && b;
                // SF's signature is (..., ulong ignoreUserID, ...) — raw ulong,
                // NOT CSteamID. So a typed cast works, but use Convert for
                // robustness against future SF refactors.
                ulong ignoreUID = UnboxUlong(__args.Length > 3 ? __args[3] : null);

                int prev;
                _p65BroadcastByType.TryGetValue(msgType, out prev);
                _p65BroadcastByType[msgType] = prev + 1;

                bool first = prev == 0;
                bool sample = first || (_p65BroadcastByType[msgType] % 60 == 0);
                if (sample)
                {
                    Log.LogInfo($"[P6.5] HostBroadcast#{_p65BroadcastCount} msgType={msgType}({MsgTypeName(msgType)}) bodyLen={data?.Length ?? 0} ignoreSrv={ignoreServer} ignoreUID={ignoreUID} count[{msgType}]={_p65BroadcastByType[msgType]}");
                }
                // For ObjectUpdate, log the index every time so we can see
                // which scene NSOs are broadcasting (the index is the first
                // 2 bytes of the body, ushort LE).
                    if (msgType == 31 && data != null && (object)Instance != null)
                        Instance.CacheGroundWeaponsBroadcast(data);
                    if (msgType == 33 && sample)
                        Log.LogInfo($"[v26.6] Host MapInfoSync forward count={_p65BroadcastByType[msgType]} bodyLen={data?.Length ?? 0}");
                    if (msgType == 26 && data != null && data.Length >= 2 && _p65ObjUpdateIdxLogCount < 30)
                {
                    ushort idx = (ushort)(data[0] | (data[1] << 8));
                    if (!_p65ObjUpdateSeenIndices.Contains(idx))
                    {
                        _p65ObjUpdateSeenIndices.Add(idx);
                        _p65ObjUpdateIdxLogCount++;
                        Log.LogInfo($"[P6.5] ObjectUpdate from new index={idx} (total unique={_p65ObjUpdateSeenIndices.Count})");
                    }
                }

                // Phase 6.5 Step 2 — forward the broadcast through our v25
                // protocol so the real client actually receives it. SF's own
                // SendMessageToAllClients loop iterates mConnectedClients which
                // is empty on the oracle (we never registered the user there),
                // so SF's loop is a no-op. We do the actual delivery here.
                // SF's MsgType enum byte values match our v25 protocol's Pkt*
                // constants 1:1 for the first 38 entries.
                //
                // Phase 6.7 filter: if the oracle's own mirror rig generates
                // PlayerUpdate (10) or PlayerTalked (12) broadcasts, do NOT
                // forward them. The client already receives a relay of the
                // real player's PlayerUpdate via HandlePlayerUpdate, and an
                // oracle-rig PlayerUpdate would appear as a phantom 2nd
                // player on the client's screen.
                if ((object)Instance != null && data != null
                    && msgType != 10  // PktPlayerUpdate
                    && msgType != 12) // PktPlayerTalked
                {
                    // Extract the channel arg (index 5 in SendMessageToAllClients).
                    // The patched DLL routes incoming packets by channel — using
                    // channel 0 for everything sends them to CheckMessageType
                    // which throws "Messagetype X is not setup!" for things like
                    // ObjectUpdate that should go to NSO.ListenForPackages instead.
                    byte channel = 0;
                    if (__args.Length > 5)
                    {
                        try { channel = (byte)Convert.ToInt32(__args[5]); } catch { }
                    }
                    bool skip = false;
                    // For ObjectUpdate, filter out broadcasts where the object's
                    // Y position is out of int16 range (overflow artifact).
                    if (msgType == 26 && data.Length >= 4)
                    {
                        short posYmul100 = (short)(data[2] | (data[3] << 8));
                        if (posYmul100 < -3000)
                        {
                            skip = true;
                            if (_p65ObjUpdateFilterCount < 5 || _p65ObjUpdateFilterCount % 100 == 0)
                                Log.LogInfo($"[P6.5] Skipping ObjectUpdate forward — Y={posYmul100/100f:0.0} out of playable range (#{_p65ObjUpdateFilterCount})");
                            _p65ObjUpdateFilterCount++;
                        }
                    }
                    // BOXES/ICE VANISHING FIX (reverted from P0-11 Y-aware
                    // heuristic 2026-05-23 night). Back to drop-ALL for
                    // server-originated destructions. The Y-aware filter
                    // introduced random ice/chain breaks because chains
                    // stress-break on the oracle's scene under joint
                    // forces (above Y=-30 obviously) and we were forwarding
                    // those.
                    //
                    // The "ghost box" tradeoff (server destroys box, client
                    // still has it) is rare and recoverable. The "ice
                    // randomly breaks" was constant during play. Pick the
                    // less-bad failure mode.
                    //
                    // Legit destructions still propagate from clients via
                    // the INBOUND RelayBodyToAll path — kicking ice as a
                    // player still works because the player rig (dynamic)
                    // colliding with the ice (kinematic on client now)
                    // fires OnCollisionEnter → SendDestructMessage → server.
                    if (msgType == 28 || msgType == 29 || msgType == 30)
                    {
                        skip = (object)Instance != null
                            && Instance.ShouldSkipServerOriginatedDestruction(data, data?.Length ?? 0);
                        if (skip)
                        {
                            if (_p65DestructionFilterCount < 5 || _p65DestructionFilterCount % 50 == 0)
                                Log.LogInfo($"[destruction] Skip server-originated msgType={msgType} (#{_p65DestructionFilterCount}) — killbox/chain-load");
                            _p65DestructionFilterCount++;
                        }
                    }
                    if (!skip) Instance.ForwardBroadcastToV25Clients(msgType, data, ignoreUID, channel);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5] broadcast prefix threw: {e.Message}");
            }
            return true; // run original (no-op on oracle because mConnectedClients is empty)
        }

        // Forward an intercepted host broadcast through our v25 UDP socket.
        // Channel is critical — the patched DLL routes by channel; non-zero
        // channels (e.g. 10 for ObjectUpdate) dispatch via NSO.ListenForPackages,
        // while channel 0 goes to P2PPackageHandler.CheckMessageType.
        internal void ForwardBroadcastToV25Clients(byte msgType, byte[] body, ulong ignoreUID, byte channel = 0)
        {
            int sent = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                if (!cli.Initialized) continue;
                if (ignoreUID != 0 && cli.SteamID == ignoreUID) continue;
                SendSfPacket(cli.Addr, msgType, body, 0uL, channel);
                sent++;
            }
            if (sent > 0 && _p65BroadcastCount <= 5)
                Log.LogInfo($"[P6.5] Forwarded msgType={msgType}({MsgTypeName(msgType)}) bodyLen={body.Length} ch={channel} to {sent} v25 client(s).");
        }
        internal static bool SendDirectPrefix(object[] __args)
        {
            try
            {
                _p65DirectCount++;
                // args: [CSteamID clientID, byte[] data, MsgType type, EP2PSend, int channel]
                ulong sid = 0uL;
                if (__args.Length > 0 && __args[0] != null)
                {
                    var idObj = __args[0];
                    var f = AccessTools.Field(idObj.GetType(), "m_SteamID");
                    if ((object)f != null) sid = (ulong)f.GetValue(idObj);
                }
                var data = __args.Length > 1 ? __args[1] as byte[] : null;
                byte msgType = UnboxByte(__args.Length > 2 ? __args[2] : null);

                int prev;
                _p65DirectByType.TryGetValue(msgType, out prev);
                _p65DirectByType[msgType] = prev + 1;

                bool first = prev == 0;
                bool sample = first || (_p65DirectByType[msgType] % 60 == 0);
                if (sample)
                {
                    Log.LogInfo($"[P6.5] DirectSend#{_p65DirectCount} → sid={sid} msgType={msgType}({MsgTypeName(msgType)}) bodyLen={data?.Length ?? 0} count[{msgType}]={_p65DirectByType[msgType]}");
                }
            }
            catch (Exception e)
            {
                Log.LogWarning($"[P6.5] direct prefix threw: {e.Message}");
            }
            return true;
        }
        internal static Vector2 QuantizeMapSyncKey(Vector2 v)
        {
            float invQ = 1f / MapSyncKeyQuantum;
            return new Vector2(
                Mathf.Round(v.x * invQ) / invQ,
                Mathf.Round(v.y * invQ) / invQ);
        }

        // Prefix on MultiplayerManager.AddMapDataObject(Vector2, MapInfoSyncableBase).
        // We can't change a struct argument by ref in a Harmony prefix on a
        // non-out parameter, but we CAN modify the dictionary via the
        // postfix side. Instead, intercept by writing the quantized key
        // back into the MapInfoSyncableBase's m_StartPos AND replacing
        // the pos arg via the __args array (Harmony allows this).
        internal static bool AddMapDataObjectPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length < 2) return true;
                if (!(__args[0] is Vector2 pos)) return true;
                var quantized = QuantizeMapSyncKey(pos);
                __args[0] = quantized;
                // Also update m_StartPos on the MapInfoSyncableBase so
                // outbound SyncMapData broadcasts the quantized key.
                if (__args[1] != null)
                {
                    var t = __args[1].GetType();
                    var f = AccessTools.Field(t, "m_StartPos");
                    if ((object)f != null) f.SetValue(__args[1], quantized);
                }
            }
            catch { }
            return true;
        }

        // Prefix on MultiplayerManager.OnMapDataRecieved(byte[]).
        // The body's first 8 bytes are the Vector2 key the server sent.
        // After our AddMapDataObjectPrefix the server's keys are already
        // quantized, so the wire's key matches our dict — no action
        // needed here. But: if for some reason the server didn't quantize
        // (older oracle, race), an inbound un-quantized key would still
        // miss our quantized dict. Rewrite the body's first 8 bytes to
        // the quantized form as a belt-and-suspenders.
        internal static bool OnMapDataRecievedPrefix(byte[] data)
        {
            try
            {
                if (data == null || data.Length < 8) return true;
                float x = BitConverter.ToSingle(data, 0);
                float y = BitConverter.ToSingle(data, 4);
                if (float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y)) return true;
                var q = QuantizeMapSyncKey(new Vector2(x, y));
                var xBytes = BitConverter.GetBytes(q.x);
                var yBytes = BitConverter.GetBytes(q.y);
                Buffer.BlockCopy(xBytes, 0, data, 0, 4);
                Buffer.BlockCopy(yBytes, 0, data, 4, 4);
            }
            catch { }
            return true;
        }
        private static void InstallMapInfoSyncQuantize()
        {
            if (_mapSyncQuantizeInstalled) return;
            _mapSyncQuantizeInstalled = true;
            try
            {
                var mgrType = CachedType("MultiplayerManager");
                if ((object)mgrType == null) { Log.LogWarning("[P0-12] MultiplayerManager not found."); return; }
                var harmony = new Harmony(PluginGuid + ".mapsync-quantize");
                var addM = AccessTools.Method(mgrType, "AddMapDataObject");
                if ((object)addM != null)
                {
                    harmony.Patch(addM, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(AddMapDataObjectPrefix))));
                    Log.LogInfo("[P0-12] Patched MultiplayerManager.AddMapDataObject (quantize Vector2 key to 0.01).");
                }
                else Log.LogWarning("[P0-12] AddMapDataObject not found.");
                var recvM = AccessTools.Method(mgrType, "OnMapDataRecieved");
                if ((object)recvM != null)
                {
                    harmony.Patch(recvM, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(OnMapDataRecievedPrefix))));
                    Log.LogInfo("[P0-12] Patched MultiplayerManager.OnMapDataRecieved (quantize inbound Vector2 key).");
                }
                else Log.LogWarning("[P0-12] OnMapDataRecieved not found.");
            }
            catch (Exception e) { Log.LogWarning($"[P0-12] install failed: {e.Message}"); }
        }

        private static void InstallClientModePatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid + ".client-shim");

                var nsoType = CachedType("NetworkSyncableObject");
                if ((object)nsoType == null) { Log.LogWarning("[CLIENT] NetworkSyncableObject type not found."); return; }

                // REVERTED 2026-05-23 night — the "dynamic NSOs locally"
                // patch (Patch 1) caused random ice/chain destruction events
                // during normal gameplay: each client's local box physics
                // would do NSO-on-NSO collisions that fire DestructiblePiece
                // .Collide → SendDestructMessage → server → all clients see
                // a spurious break.
                //
                // Stock SF kinematic NSOs on clients:
                //   - NSO-on-NSO collisions don't fire OnCollisionEnter
                //     (both bodies kinematic — no contact resolution)
                //   - Player-on-NSO collisions still fire (player rig is
                //     dynamic) — so kicking ice still destructs correctly,
                //     just via the server-relay round-trip
                //   - Cost: pushing a box has ~RTT latency before it
                //     visually moves locally (the v26 snapshot drives it)
                //
                // Net: stable destruction model > instant push feedback.
                // Tradeoff is the same one P0-5's original fix took.
                Log.LogInfo("[CLIENT] Stock-default kinematic NSOs (no DisableAllRigidBodies skip — prevents spurious NSO-on-NSO destruction events).");
            }
            catch (Exception e)
            {
                Log.LogError($"[CLIENT] Client-mode shim install failed: {e}");
            }
        }
        // Generic skip-prefix: return false to skip the original method.
        internal static bool SkipPrefix() => false;
        internal static bool IsPacketAvailableHeadlessPrefix(object __instance, int channel, ref bool __result)
        {
            if (!_batchModeHost) return true;
            try
            {
                if (!_ppChannelsLookupTried)
                {
                    _ppChannelsLookupTried = true;
                    _ppChannelsField = AccessTools.Field(__instance.GetType(), "channels");
                }
                var chField = _ppChannelsField;
                if ((object)chField == null) { __result = false; return false; }
                var channels = chField.GetValue(__instance) as Array;
                if (channels == null || channel < 0 || channel >= channels.Length)
                {
                    __result = false;
                    return false;
                }
                if (channels.GetValue(channel) == null)
                {
                    __result = false;
                    return false;
                }
            }
            catch
            {
                __result = false;
                return false;
            }
            return true;
        }
        private static void TryPatch(Harmony harmony, string label, MethodInfo target, string prefix = null, string postfix = null)
        {
            _p65PatchesAttempted++;
            if ((object)target == null)
            {
                _p65MissingPatches.Add($"{label} — target method not found");
                Log.LogError($"[P6.5] SKIP {label} — target method not found.");
                return;
            }
            try
            {
                var pfx = prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(Plugin), prefix)) : null;
                var pst = postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(Plugin), postfix)) : null;
                harmony.Patch(target, prefix: pfx, postfix: pst);
                _p65PatchesSucceeded++;
                Log.LogInfo($"[P6.5] Patched {label}.");
            }
            catch (Exception e)
            {
                _p65MissingPatches.Add($"{label} — {e.GetType().Name}: {e.Message}");
                Log.LogError($"[P6.5] FAIL {label}: {e}");
            }
        }

        // Safe-unbox helpers. Mono's runtime is permissive about typed casts on
        // boxed enums, but a direct `(byte)box` can throw InvalidCastException
        // on a stricter CLR. Convert.* uses IConvertible which handles both
        // raw primitives and byte-backed enums uniformly.
        private static byte UnboxByte(object o)
        {
            if (o == null) return (byte)255;
            try { return (byte)Convert.ToInt32(o); } catch { return (byte)255; }
        }
        private static ulong UnboxUlong(object o)
        {
            if (o == null) return 0uL;
            try { return Convert.ToUInt64(o); } catch { return 0uL; }
        }

        // SF MsgType enum (P2PPackageHandler.MsgType byte values, from decompile).
        private static string MsgTypeName(byte b) => b switch
        {
            0  => "Ping",
            1  => "PingResponse",
            2  => "ClientJoined",
            3  => "ClientRequestingAccepting",
            4  => "ClientAccepted",
            5  => "ClientInit",
            6  => "ClientRequestingIndex",
            7  => "ClientRequestingToSpawn",
            8  => "ClientSpawned",
            9  => "ClientReadyUp",
            10 => "PlayerUpdate",
            11 => "PlayerTookDamage",
            12 => "PlayerTalked",
            13 => "PlayerForceAdded",
            14 => "PlayerForceAddedAndBlock",
            15 => "PlayerLavaForceAdded",
            16 => "PlayerFallOut",
            17 => "PlayerWonWithRicochet",
            18 => "MapChange",
            19 => "WeaponSpawned",
            20 => "WeaponThrown",
            21 => "RequestingWeaponThrow",
            22 => "ClientRequestWeaponDrop",
            23 => "WeaponDropped",
            24 => "WeaponWasPickedUp",
            25 => "ClientRequestingWeaponPickUp",
            26 => "ObjectUpdate",
            27 => "ObjectSpawned",
            28 => "ObjectSimpleDestruction",
            29 => "ObjectInvokeDestructionEvent",
            30 => "ObjectDestructionCollision",
            31 => "GroundWeaponsInit",
            32 => "MapInfo",
            33 => "MapInfoSync",
            34 => "WorkshopMapsLoaded",
            35 => "StartMatch",
            36 => "ObjectHello",
            37 => "OptionsChanged",
            38 => "KickPlayer",
            _  => "?"
        };

        private static float ReadAxis(object actions, string fieldName, string axis)
        {
            try
            {
                var f = AccessTools.Field(actions.GetType(), fieldName);
                if ((object)f == null) return 0f;
                var ctrl = f.GetValue(actions);
                if ((object)ctrl == null) return 0f;
                var t = ctrl.GetType();
                var backing = AccessTools.Field(t, "<" + axis + ">k__BackingField");
                if ((object)backing != null) return (float)backing.GetValue(ctrl);
                var prop = AccessTools.Property(t, axis);
                if ((object)prop != null) return (float)prop.GetValue(ctrl, null);
            }
            catch { }
            return float.NaN;
        }

        // Force the named TwoAxisInputControl's X/Y to (x, y) by writing
        // its private fields. Run from the Harmony prefix on Controller.Update.
        private static void ForceTwoAxis(object actions, string fieldName, float x, float y)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var ctrl = f.GetValue(actions);
            if ((object)ctrl == null) return;
            var t = ctrl.GetType();
            var thisValueField = AccessTools.Field(t, "thisValue");
            if ((object)thisValueField != null) thisValueField.SetValue(ctrl, new Vector2(x, y));
            var xBacking = AccessTools.Field(t, "<X>k__BackingField");
            var yBacking = AccessTools.Field(t, "<Y>k__BackingField");
            if ((object)xBacking != null) xBacking.SetValue(ctrl, x);
            if ((object)yBacking != null) yBacking.SetValue(ctrl, y);
        }

        // Force a button's IsPressed / Value via InputControlState struct
        // backing fields. PlayerAction.thisState is an InputControlState
        // struct with public bool State and public float Value; reading
        // IsPressed returns thisState.State.
        private static void ForceButton(object actions, string fieldName, bool pressed)
        {
            var f = AccessTools.Field(actions.GetType(), fieldName);
            if ((object)f == null) return;
            var pa = f.GetValue(actions);
            if ((object)pa == null) return;
            // OneAxisInputControl has private thisState field.
            var thisStateField = AccessTools.Field(pa.GetType(), "thisState");
            if ((object)thisStateField == null) return;
            // thisState is a struct. We have to box, mutate, write back.
            object state = thisStateField.GetValue(pa);
            if ((object)state == null) return;
            var stateType = state.GetType();
            var stateField = AccessTools.Field(stateType, "State");
            var valueField = AccessTools.Field(stateType, "Value");
            if ((object)stateField != null) stateField.SetValue(state, pressed);
            if ((object)valueField != null) valueField.SetValue(state, pressed ? 1.0f : 0.0f);
            thisStateField.SetValue(pa, state);
        }

        private void TryPatchHealthHandlerDieForRoundAdvance(Harmony harmony)
        {
            try
            {
                var hhType = CachedType("HealthHandler");
                if ((object)hhType == null) { Log.LogWarning("[DEATH] HealthHandler type not found."); return; }
                var dieMethod = AccessTools.Method(hhType, "Die");
                if ((object)dieMethod == null) dieMethod = AccessTools.Method(hhType, "OnDeath");
                if ((object)dieMethod == null) { Log.LogWarning("[DEATH] HealthHandler.Die not found."); return; }
                var postfix = AccessTools.Method(typeof(Plugin), nameof(HealthHandlerDiePostfix));
                harmony.Patch(dieMethod, postfix: new HarmonyMethod(postfix));
                Log.LogInfo("[DEATH] Patched HealthHandler.Die — schedules round advance when isDead.");
            }
            catch (Exception e) { Log.LogWarning($"[DEATH] HealthHandler patch failed: {e.Message}"); }
        }

        private static void HealthHandlerDiePostfix(object __instance)
        {
            if ((object)Instance == null) return;
            Instance.OnOraclePlayerDied(__instance, "HealthHandler.Die");
        }

        // Harmony postfix on NetworkSocketServer ctor. The stock ctor sets
        // Server = new NetServer(config{Port = 1337}). We can't easily unwind
        // that, but Lidgren NetServer hasn't been Start()ed yet at this point —
        // we can mutate its Configuration.Port before Init() is called.
        private static void PatchServerPort(object __instance)
        {
            try
            {
                var serverProp = AccessTools.Property(__instance.GetType(), "Server");
                if ((object)serverProp == null) return;
                var netServer = serverProp.GetValue(__instance, null);
                if ((object)netServer == null) return;
                var configProp = AccessTools.Property(netServer.GetType(), "Configuration");
                if ((object)configProp == null) return;
                var config = configProp.GetValue(netServer, null);
                if ((object)config == null) return;
                var portProp = AccessTools.Property(config.GetType(), "Port");
                if ((object)portProp == null) return;
                portProp.SetValue(config, BindPort, null);
                Log.LogInfo($"NetworkSocketServer ctor postfix: rewrote Port → {BindPort}.");
            }
            catch (Exception e)
            {
                Log.LogError($"PatchServerPort threw: {e}");
            }
        }
    }
}
