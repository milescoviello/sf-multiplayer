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

        private static void OnSceneLoadedTeleport(Scene scene, LoadSceneMode mode)
        {
            if (!_pendingTeleportArmed) return;
            _pendingTeleportArmed = false;
            SceneManager.sceneLoaded -= OnSceneLoadedTeleport;
            Log.LogInfo($"OnSceneLoadedTeleport: scene={scene.name} target={_pendingTeleport}; teleporting {SlotToRig.Count} rigs.");
            foreach (var kv in SlotToRig)
            {
                if ((object)kv.Value == null) continue;
                TeleportRig(kv.Value, _pendingTeleport);
            }
        }

        // TeleportRig moves the rig root + every BodyPart Rigidbody to the
        // target position. The root transform alone doesn't move the visible
        // rig (body parts have independent Rigidbody-driven positions); we
        // have to relocate them all and zero their velocity so they don't
        // immediately bounce back to the old location.
        private static void TeleportRig(GameObject rig, Vector3 target)
        {
            try
            {
                var rootPos = rig.transform.position;
                var delta = target - rootPos;
                rig.transform.position = target;

                var bpType = CachedType("BodyPart");
                if ((object)bpType == null) return;
                var bps = rig.GetComponentsInChildren(bpType);
                int moved = 0;
                foreach (var bp in bps)
                {
                    var bpComp = bp as Component;
                    if ((object)bpComp == null) continue;
                    var rb = bpComp.GetComponent<Rigidbody>();
                    if ((object)rb == null) continue;
                    rb.position = rb.position + delta;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    bpComp.transform.position = bpComp.transform.position + delta;
                    moved++;
                }
                Log.LogInfo($"TeleportRig: moved {moved} body parts by delta={delta}");
            }
            catch (Exception e)
            {
                Log.LogError($"TeleportRig threw: {e.Message}");
            }
        }

        private void SpawnAuthoritativePlayersForAllClients()
        {
            Log.LogInfo($"[P6.9] SpawnAuthoritativePlayers: iterating {_sfClients.Count} clients.");
            // [SF DEBUG] dump every SfClient's state before we spawn — this
            // is the ground-truth snapshot of who-is-who at this moment.
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                Log.LogInfo($"[SF DEBUG]   _sfClients[{kv.Key}] → Slot={c.Slot} SteamID={c.SteamID} Spawned={c.Spawned} Initialized={c.Initialized}");
            }
            int considered = 0, spawned = 0, skipped = 0;
            foreach (var kv in _sfClients)
            {
                var cli = kv.Value;
                considered++;
                if (!cli.Initialized)
                {
                    Log.LogInfo($"[P6.9] Skip {kv.Key}: not Initialized.");
                    skipped++;
                    continue;
                }
                if (cli.IsSpectator) { skipped++; continue; }   // spectators have no body
                if (SlotToRig.ContainsKey(cli.Slot))
                {
                    Log.LogInfo($"[P6.9] Skip {kv.Key}: rig already exists for slot {cli.Slot}.");
                    skipped++;
                    continue;
                }
                Vector3 startPos = new Vector3(0f, 8f, 0f);
                bool ok = TrySpawnPlayer(cli.Slot, startPos, out string err);
                if (ok)
                {
                    Log.LogInfo($"[P6.9] Spawned authoritative rig for client slot={cli.Slot} steamID={cli.SteamID}.");
                    ConfigureAuthoritativeRig(cli.Slot);
                    spawned++;
                }
                else
                {
                    Log.LogError($"[P6.9] Failed to spawn authoritative rig for slot {cli.Slot}: {err}");
                }
            }
            Log.LogInfo($"[P6.9] SpawnAuthoritativePlayers done: considered={considered} spawned={spawned} skipped={skipped}");
        }

        // Configure a freshly-spawned rig as the server's authoritative copy
        // of that player. Per-instance HasControl=true on the Controller so
        // SF's host-side gates (destructible piece OnCollisionEnter, etc.)
        // accept this rig as a legitimate authority source.
        //
        // Also configure as a "physics ghost" — kinematic body parts that
        // get teleported to client position each PlayerUpdate. The ghost
        // pushes NSOs (boxes/crates) via kinematic sweep so box collisions
        // happen server-side, then NSO snapshots broadcast box positions
        // back to all clients. NSO components on the rig itself are
        // disabled so they don't broadcast wrong indices.
        //
        // This is "mirror rig 2.0" — real NetworkPlayer with per-instance
        // HasControl, behaving as a ghost until v26 PlayerInput properly
        // drives Movement.cs. The transition will be: when inputs are
        // verified flowing reliably, un-kinematic the root and the rig
        // becomes input-driven (no more position-from-client mirror).
        private void ConfigureAuthoritativeRig(int slot)
        {
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            try
            {
                var ctrlType = CachedType("Controller");
                if ((object)ctrlType != null)
                {
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var hasCtrlF = AccessTools.Field(ctrlType, "mHasControl");
                        if ((object)hasCtrlF != null)
                        {
                            // Vanilla host: a remote player's Controller has no
                            // control; it replays the movement state from that
                            // player's PlayerUpdate (ApplyMovementState).
                            hasCtrlF.SetValue(ctrl, LegacyRigs);
                            Log.LogInfo($"[P6.9] Slot {slot}: Controller.mHasControl set {LegacyRigs} (per-instance).");
                        }
                    }
                }

                // Make all body part rigidbodies kinematic — no gravity, no
                // Movement-driven forces, just position-driven sweeps.
                var rbs = rig.GetComponentsInChildren<Rigidbody>();
                int kinSet = 0;
                foreach (var rb in rbs)
                {
                    if ((object)rb == null) continue;
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    // Parked until its player's first PlayerUpdate places it:
                    // the rig spawns at a fixed point that can sit inside map
                    // props (Desert5's centre crate stack), and a kinematic
                    // body there shoves them apart.
                    if (!LegacyRigs) rb.detectCollisions = false;
                    kinSet++;
                }

                // Disable NSO components on the rig — they'd otherwise
                // broadcast ObjectUpdate with whatever Index the rig parts
                // carry, potentially corrupting scene-object indices on
                // clients.
                int nsoOff = 0;
                var nsoType = CachedType("NetworkSyncableObject");
                if ((object)nsoType != null)
                {
                    var nsos = rig.GetComponentsInChildren(nsoType);
                    foreach (var nso in nsos)
                    {
                        var beh = nso as Behaviour;
                        if ((object)beh != null) { beh.enabled = false; nsoOff++; }
                    }
                }
                Log.LogInfo($"[P6.9 ghost] Slot {slot}: {kinSet} rbs kinematic, {nsoOff} NSO components disabled.");
            }
            catch (Exception e) { Log.LogWarning($"[P6.9 ConfigureAuthoritativeRig] {e.Message}"); }
        }

        // The server's copy of each player is what a vanilla host has for a
        // remote player (NetworkPlayer.SyncClientState + LerpLocalDummy):
        //  - parked (kinematic, no collisions) until that player's first
        //    PlayerUpdate, then every body goes dynamic, a real ragdoll with
        //    the player's real mass (162 total vs 500-1500 per crate);
        //  - its Controller replays the movement state from PlayerUpdate
        //    (look rotation, left/right/jump bits, stick Y), so it stands,
        //    walks and jumps like the player;
        //  - after each PlayerUpdate the root is eased toward root + (reported
        //    - body average) for one send interval: transform.position =
        //    Lerp(pos, end, dt * 10) (clients report the body average).
        // It used to be a kinematic statue (infinite mass) teleported to the
        // reported point with its body 1.2u above the player's: walking into,
        // standing on, or spawning inside a crate stack shoved every crate at
        // full speed ("stacks collapse as soon as players fight on them").
        // SF_LEGACY_RIGS=1 restores the kinematic ghost.
        private static readonly bool LegacyRigs = Environment.GetEnvironmentVariable("SF_LEGACY_RIGS") == "1";
        private const float DummyLerpTime = 1f / 50f;   // NetworkPlayer.mSendRate (mSendRatePerSecond = 50)
        private readonly Dictionary<int, Vector3> _rigTarget = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> _rigLerpStart = new Dictionary<int, float>();
        private readonly HashSet<int> _rigLive = new HashSet<int>();   // rig instance ids that got their first PlayerUpdate
        private static MethodInfo _ctrlSetLookM, _ctrlSetMoveM, _standSetStickM;
        private static Type _standingType;

        // FixedUpdate: LerpLocalDummy for every rig that got a PlayerUpdate
        // within the last send interval.
        private void TickRigLerp()
        {
            if (_rigTarget.Count == 0) return;
            float k = Time.deltaTime * 10f;
            List<int> done = null;
            foreach (var kv in _rigTarget)
            {
                GameObject rig;
                if (!SlotToRig.TryGetValue(kv.Key, out rig) || (object)rig == null) { (done ?? (done = new List<int>())).Add(kv.Key); continue; }
                rig.transform.position = Vector3.Lerp(rig.transform.position, kv.Value, k);
                float t0;
                if (!LegacyRigs && _rigLerpStart.TryGetValue(kv.Key, out t0) && Time.time - t0 >= DummyLerpTime)
                    (done ?? (done = new List<int>())).Add(kv.Key);
            }
            if (done != null) foreach (var s in done) _rigTarget.Remove(s);
        }

        private void DriveRigFromPlayerUpdate(int slot, byte[] body, Vector3 reported)
        {
            if (LegacyRigs) { UpdateGhostRigPosition(slot, reported); return; }
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            if (_deadThisRound.Contains(slot)) return;   // died: stays parked
            var rbs = rig.GetComponentsInChildren<Rigidbody>();
            if (rbs.Length == 0) return;
            Vector3 avg = Vector3.zero;
            foreach (var rb in rbs) avg += rb.transform.position;
            avg /= rbs.Length;
            Vector3 diff = reported - avg;
            int id = rig.GetInstanceID();
            if (!_rigLive.Contains(id) || diff.sqrMagnitude > 25f)
            {
                // First package of this life (or a jump > 5u: respawn, map
                // change): put the body where the player is, then go dynamic.
                rig.transform.position = rig.transform.position + diff;
                foreach (var rb in rbs)
                {
                    if ((object)rb == null) continue;
                    rb.isKinematic = false;
                    rb.detectCollisions = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
                _rigTarget.Remove(slot);
                if (_rigLive.Add(id))
                    Log.LogInfo($"[P6.9 dummy] slot {slot}: first PlayerUpdate, body placed at ({reported.y:0.00},{reported.z:0.00}) and dynamic ({rbs.Length} bodies).");
            }
            else
            {
                _rigTarget[slot] = rig.transform.position + diff;
                _rigLerpStart[slot] = Time.time;
            }
            ApplyDummyMovementState(rig, body);
            ReplayDummyFight(rig, body);
        }

        // The rest of SyncClientState: fight state, the shots fired since the
        // last update, and the weapon held. A stock host replays every remote
        // player's shots this way, and that copy is what makes a grenade blast
        // push the host's crates and what spawns a snake-gun snake on the
        // host (registered under the shooter's sync index), the snake the host
        // then aims (SnakeAIUpdatePrefix). The server never replayed them, so
        // snake-gun snakes never chased anyone. The replayed shots can't hurt
        // or push anything over the network: every such path is gated on the
        // shooter's Controller.HasControl, which a dummy doesn't have.
        // Body after the movement byte: [u8 fightState][u16 n] n x [i16 posY]
        // [i16 posZ][s8 dirY][s8 dirZ][u16 syncIndex], then [u8 weapon+1].
        private static readonly bool ReplayShots = Environment.GetEnvironmentVariable("SFHEADLESS_REPLAY_SHOTS") != "0";
        private const int MaxReplayedShots = 24;
        private static Type _pkgT, _svT, _bvT, _fightingT;
        private static FieldInfo _pkgPosF, _pkgVecF, _pkgSyncF;
        private static MethodInfo _fSetStateM, _fFireM, _fPickM;
        private static bool _replayReflTried;
        private int _replayedShots;
        private ushort _fireTestSync = 50000;

        // Bridge "fireTest" (test tool): the server's copy of player <slot>
        // takes weapon byte <weapon> (weapon index + 1, as PlayerUpdate sends
        // it) and fires one shot at the nearest other player, through the same
        // replay path as a real shot (two crafted PlayerUpdate bodies).
        internal string FireTest(int slot, int weapon)
        {
            GameObject rig;
            if (!SlotToRig.TryGetValue(slot, out rig) || (object)rig == null || !rig) return "no rig for slot " + slot;
            Vector3 from = rig.transform.position;
            var hipT = CachedType("Hip");
            var hip = (object)hipT != null ? rig.GetComponentInChildren(hipT) as Component : null;
            if ((object)hip != null && hip) from = hip.transform.position;
            Vector3 to = from + new Vector3(0f, 0f, 5f);
            foreach (var kv in SlotToRig)
            {
                if (kv.Key == slot || (object)kv.Value == null || !kv.Value) continue;
                var h = (object)hipT != null ? kv.Value.GetComponentInChildren(hipT) as Component : null;
                to = ((object)h != null && h) ? h.transform.position : kv.Value.transform.position;
                break;
            }
            Vector3 dir = (to - from); dir.x = 0f; dir = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.forward;
            Vector3 muzzle = from + dir * 1.2f + Vector3.up * 0.3f;
            byte[] pick = new byte[12];
            pick[8] = 0; pick[11] = (byte)weapon;
            ReplayDummyFight(rig, pick);
            byte[] shot = new byte[20];
            shot[8] = 1; shot[9] = 1; shot[10] = 0;
            short py = (short)Mathf.RoundToInt(muzzle.y * 100f), pz = (short)Mathf.RoundToInt(muzzle.z * 100f);
            shot[11] = (byte)py; shot[12] = (byte)(py >> 8); shot[13] = (byte)pz; shot[14] = (byte)(pz >> 8);
            shot[15] = (byte)(sbyte)Mathf.RoundToInt(dir.y * 100f); shot[16] = (byte)(sbyte)Mathf.RoundToInt(dir.z * 100f);
            ushort sync = _fireTestSync++;
            shot[17] = (byte)sync; shot[18] = (byte)(sync >> 8);
            shot[19] = (byte)weapon;
            ReplayDummyFight(rig, shot);
            var fT = CachedType("Fighting");
            var held = (object)fT != null ? AccessTools.Field(fT, "weapon") : null;
            var f = (object)fT != null ? rig.GetComponent(fT) : null;
            object w = ((object)held != null && (object)f != null) ? held.GetValue(f) : null;
            return "slot " + slot + " fired weapon " + weapon + " from " + muzzle.ToString("0.0") + " dir " + dir.ToString("0.00") + " sync " + sync + " held=" + (w != null ? ((Component)w).name : "none");
        }

        private void ReplayDummyFight(GameObject rig, byte[] body)
        {
            if (!ReplayShots || body == null || body.Length < 12) return;
            try
            {
                if (!_replayReflTried)
                {
                    _replayReflTried = true;
                    _pkgT = CachedType("ProjectilePackageStruct"); _svT = CachedType("ShortVector2"); _bvT = CachedType("ByteVector2");
                    _fightingT = CachedType("Fighting");
                    if ((object)_pkgT != null)
                    {
                        _pkgPosF = AccessTools.Field(_pkgT, "shootPosition"); _pkgVecF = AccessTools.Field(_pkgT, "shootVector"); _pkgSyncF = AccessTools.Field(_pkgT, "syncIndex");
                    }
                    if ((object)_fightingT != null)
                    {
                        _fSetStateM = AccessTools.Method(_fightingT, "SetFightState");
                        _fFireM = AccessTools.Method(_fightingT, "FirePackages");
                        _fPickM = AccessTools.Method(_fightingT, "NetworkPickUpWeapon");
                    }
                    if ((object)_pkgPosF == null || (object)_fFireM == null || (object)_fPickM == null || (object)_fSetStateM == null || (object)_svT == null || (object)_bvT == null)
                        Log.LogWarning("[P6.9 dummy] shot replay: reflection incomplete, off.");
                }
                if ((object)_fFireM == null || (object)_fPickM == null || (object)_fSetStateM == null || (object)_pkgPosF == null) return;
                byte state = body[8];
                int n = body[9] | (body[10] << 8);
                if (n > MaxReplayedShots || body.Length < 11 + n * 8 + 1) return;
                byte weapon = body[11 + n * 8];
                var fighting = rig.GetComponent(_fightingT);
                if ((object)fighting == null) return;
                var shots = Array.CreateInstance(_pkgT, n);
                for (int i = 0; i < n; i++)
                {
                    int o = 11 + i * 8;
                    object pkg = Activator.CreateInstance(_pkgT);
                    _pkgPosF.SetValue(pkg, Activator.CreateInstance(_svT, new object[] { (short)(body[o] | (body[o + 1] << 8)), (short)(body[o + 2] | (body[o + 3] << 8)) }));
                    _pkgVecF.SetValue(pkg, Activator.CreateInstance(_bvT, new object[] { (sbyte)body[o + 4], (sbyte)body[o + 5] }));
                    _pkgSyncF.SetValue(pkg, (ushort)(body[o + 6] | (body[o + 7] << 8)));
                    shots.SetValue(pkg, i);
                }
                _fSetStateM.Invoke(fighting, new object[] { state });
                if (state == 1)
                {
                    _fFireM.Invoke(fighting, new object[] { shots, weapon });
                    _replayedShots += n;
                    if (n > 0 && (_replayedShots <= 5 || _replayedShots % 200 == 0))
                        Log.LogInfo($"[P6.9 dummy] replayed {n} shot(s), weapon {weapon} (total {_replayedShots})");
                }
                if ((state == 1 && weapon != 0) || state == 0)
                    _fPickM.Invoke(fighting, new object[] { weapon });
            }
            catch (Exception e)
            {
                if (_replayedShots++ < 3) Log.LogWarning($"[P6.9 dummy] shot replay: {(e.InnerException ?? e).Message}");
            }
        }

        // PlayerUpdate body (NetworkPlayer.SendNewClientStatePackage):
        // [i16 y][i16 z][s8 lookX][s8 lookY][s8 stickY][u8 movementType]...
        private static void ApplyDummyMovementState(GameObject rig, byte[] body)
        {
            if (body == null || body.Length < 8) return;
            try
            {
                var ctrlType = CachedType("Controller");
                if ((object)ctrlType == null) return;
                if ((object)_ctrlSetLookM == null)
                {
                    _ctrlSetLookM = AccessTools.Method(ctrlType, "SetNewLookRotation");
                    _ctrlSetMoveM = AccessTools.Method(ctrlType, "SetNewMovementType");
                    _standingType = CachedType("Standing");
                    if ((object)_standingType != null) _standSetStickM = AccessTools.Method(_standingType, "SetNewLeftStickYValue");
                }
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) return;
                var look = new Vector2((sbyte)body[4] / 100f, (sbyte)body[5] / 100f);
                if ((object)_ctrlSetLookM != null && look.sqrMagnitude > 0.0001f) _ctrlSetLookM.Invoke(ctrl, new object[] { look });
                if ((object)_ctrlSetMoveM != null) _ctrlSetMoveM.Invoke(ctrl, new object[] { body[7] });
                var standing = (object)_standingType != null ? rig.GetComponent(_standingType) : null;
                if ((object)standing != null && (object)_standSetStickM != null) _standSetStickM.Invoke(standing, new object[] { (sbyte)body[6] / 100f });
            }
            catch (Exception e) { if (Verbose) Log.LogDebug($"[P6.9 dummy] movement state: {e.Message}"); }
        }

        // A player died: take their server body out of the world (it would
        // otherwise stand where they died, a wall nobody can see).
        private void ParkRig(int slot)
        {
            if (LegacyRigs || !SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            _rigTarget.Remove(slot);
            foreach (var rb in rig.GetComponentsInChildren<Rigidbody>())
            {
                if ((object)rb == null) continue;
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
        }

        // SF_LEGACY_RIGS=1: the old kinematic ghost.
        private void UpdateGhostRigPosition(int slot, Vector3 target)
        {
            if (!SlotToRig.TryGetValue(slot, out var rig) || (object)rig == null) return;
            var rootPos = rig.transform.position;
            var delta = target - rootPos;
            if (delta.magnitude <= 5f)
            {
                _rigTarget[slot] = target;   // eased in FixedUpdate (TickRigLerp)
                return;
            }
            _rigTarget.Remove(slot);          // big jump (spawn / map change): snap
            rig.transform.position = target;
            foreach (var rb in rig.GetComponentsInChildren<Rigidbody>())
            {
                if ((object)rb == null) continue;
                rb.position = rb.position + delta;
                if (!rb.isKinematic) rb.velocity = Vector3.zero;
            }
        }

        // TrySpawnPlayer instantiates a Player rig in the active scene at the
        // slot's spawn point, by grabbing ControllerHandler.playerPrefab and
        // calling Object.Instantiate directly. This sidesteps the InputDevice
        // pairing path (which requires real input hardware) — the rig will
        // exist but won't move until we inject inputs.
        //
        // Returns (true, "") on success or (false, "reason") on failure.
        private void TryCachePlayerPrefab()
        {
            if ((object)_cachedPlayerPrefab != null) return;
            try
            {
                var chType = CachedType("ControllerHandler");
                if ((object)chType == null) { Log.LogWarning("CachePrefab: ControllerHandler type missing"); return; }
                var chInst = UnityEngine.Object.FindObjectOfType(chType);
                if ((object)chInst == null) { Log.LogWarning("CachePrefab: no ControllerHandler instance in active scene"); return; }
                var pf = AccessTools.Field(chType, "playerPrefab");
                if ((object)pf == null) { Log.LogWarning("CachePrefab: playerPrefab field missing"); return; }
                var go = pf.GetValue(chInst) as GameObject;
                if ((object)go == null) { Log.LogWarning("CachePrefab: playerPrefab value is null"); return; }
                _cachedPlayerPrefab = go;
                Log.LogInfo($"CachePrefab: cached playerPrefab '{go.name}' for cross-scene spawns.");
            }
            catch (Exception e) { Log.LogError($"TryCachePlayerPrefab threw: {e.Message}"); }
        }

        private bool TrySpawnPlayer(int slot, Vector3 spawnPosOverride, out string err)
        {
            err = "";
            try
            {
                GameObject prefab = _cachedPlayerPrefab;
                if ((object)prefab == null)
                {
                    var chType = CachedType("ControllerHandler");
                    if ((object)chType == null) { err = "ControllerHandler type not found"; return false; }
                    var chInst = UnityEngine.Object.FindObjectOfType(chType);
                    if ((object)chInst == null) { err = "ControllerHandler instance not in scene (and no cached prefab)"; return false; }
                    var prefabField = AccessTools.Field(chType, "playerPrefab");
                    if ((object)prefabField == null) { err = "playerPrefab field not found"; return false; }
                    prefab = prefabField.GetValue(chInst) as GameObject;
                    if ((object)prefab == null) { err = "playerPrefab is null"; return false; }
                    _cachedPlayerPrefab = prefab;
                    Log.LogInfo("Cached playerPrefab for cross-scene spawns.");
                }
                var spawnPos = spawnPosOverride; // caller-supplied; defaults to (0,8,0) in bridge handler
                var go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.identity) as GameObject;
                if ((object)go == null) { err = "Instantiate returned null"; return false; }
                go.name = $"OracleSpawn_Slot{slot}";
                // Survive SceneManager.LoadScene switches. Without this, the
                // rig is destroyed when we transition from MainScene (where
                // ControllerHandler lives, needed to spawn the rig) to a
                // Landfall scene (which has real platforms but no spawn
                // infrastructure).
                UnityEngine.Object.DontDestroyOnLoad(go);

                // Bind a fresh CharacterActions so the Controller has somewhere
                // to read input from. Without this, mPlayerActions is null and
                // the Controller.Update path early-returns / no movement.
                //
                // Stock ControllerHandler.CreatePlayer calls AssignNewDevice
                // (which requires a real InputDevice we can't synthesize),
                // but Controller also exposes TakeLocalControl(CharacterActions)
                // which doesn't need a device — perfect for our bridge-driven
                // input flow.
                var ctrlType = CachedType("Controller");
                var caType = CachedType("CharacterActions");
                if ((object)ctrlType != null && (object)caType != null)
                {
                    var ctrl = go.GetComponent(ctrlType);
                    if ((object)ctrl != null)
                    {
                        var createMethod = AccessTools.Method(caType, "CreateWithControllerBindings");
                        if ((object)createMethod != null)
                        {
                            var actions = createMethod.Invoke(null, null);
                            var takeMethod = AccessTools.Method(ctrlType, "TakeLocalControl");
                            if ((object)actions != null && (object)takeMethod != null)
                            {
                                takeMethod.Invoke(ctrl, new object[] { actions });
                                // Also assign a playerID so any code reading
                                // controller.playerID gets a sensible slot.
                                var pidField = AccessTools.Field(ctrlType, "playerID");
                                if ((object)pidField != null) pidField.SetValue(ctrl, slot);
                                Log.LogInfo($"Bound CharacterActions to slot {slot} via TakeLocalControl.");
                            }
                            else
                            {
                                Log.LogWarning("Could not bind CharacterActions: CreateWith* returned null or TakeLocalControl missing.");
                            }
                        }
                    }
                }

                SlotToRig[slot] = go;
                if (!SlotInputs.ContainsKey(slot))
                {
                    SlotInputs[slot] = new InputFrame();
                }

                // Clear regularBindings on every underlying PlayerAction in
                // this CharacterActions instance. InControl's PlayerAction.
                // UpdateBindings loops over regularBindings each frame and
                // calls UpdateWithValue(bindingSource.GetValue(Device), ...),
                // which writes 0 because we have no real device — that's what
                // clobbers our manually-injected values. With no bindings,
                // the loop is a no-op and our UpdateWithValue calls survive.
                ClearAllPlayerActionBindings(go);

                Log.LogInfo($"Spawned oracle player rig for slot {slot} at {spawnPos} (GO: {go.name})");
                return true;
            }
            catch (Exception e)
            {
                err = e.Message;
                return false;
            }
        }

        // ClearAllPlayerActionBindings walks the rig's CharacterActions and
        // clears each PlayerAction's regularBindings list. Required so our
        // per-frame UpdateWithValue calls aren't immediately overwritten by
        // InControl's UpdateBindings loop reading from null devices.
        private static void ClearAllPlayerActionBindings(GameObject rig)
        {
            try
            {
                var ctrlType = CachedType("Controller");
                if ((object)ctrlType == null) return;
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) return;
                var actionsField = AccessTools.Field(ctrlType, "mPlayerActions");
                if ((object)actionsField == null) return;
                var actions = actionsField.GetValue(ctrl);
                if ((object)actions == null) return;

                var paType = CachedType("InControl.PlayerAction");
                if ((object)paType == null) return;
                var bindingsField = AccessTools.Field(paType, "regularBindings");
                var visibleField  = AccessTools.Field(paType, "visibleBindings");
                if ((object)bindingsField == null) return;

                // Walk every field on the CharacterActions instance; any
                // PlayerAction we find, clear its bindings.
                int cleared = 0;
                foreach (var f in actions.GetType().GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var v = f.GetValue(actions);
                    if ((object)v == null) continue;
                    if (!paType.IsInstanceOfType(v)) continue;
                    var listObj = bindingsField.GetValue(v);
                    var clearMethod = listObj?.GetType().GetMethod("Clear");
                    clearMethod?.Invoke(listObj, null);
                    if ((object)visibleField != null)
                    {
                        var visObj = visibleField.GetValue(v);
                        visObj?.GetType().GetMethod("Clear")?.Invoke(visObj, null);
                    }
                    cleared++;
                }
                Log.LogInfo($"Cleared regularBindings on {cleared} PlayerActions.");
            }
            catch (Exception e)
            {
                Log.LogError($"ClearAllPlayerActionBindings: {e.Message}");
            }
        }
        private static MethodInfo GetUpdateWithValueMethod()
        {
            if ((object)_cachedUpdateWithValue != null) return _cachedUpdateWithValue;
            var paType = CachedType("InControl.PlayerAction");
            if ((object)paType == null)
            {
                if (!_loggedUpdateWithValue) { Log.LogWarning("UpdateWithValue: no InControl.PlayerAction type"); _loggedUpdateWithValue = true; }
                return null;
            }
            _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue",
                new Type[] { typeof(float), typeof(ulong), typeof(float) });
            if ((object)_cachedUpdateWithValue == null && !_loggedUpdateWithValue)
            {
                Log.LogWarning("UpdateWithValue: method not found on PlayerAction. Trying without param-type filter…");
                _cachedUpdateWithValue = AccessTools.Method(paType, "UpdateWithValue");
                if ((object)_cachedUpdateWithValue == null) Log.LogWarning("UpdateWithValue: not found even without filter");
                else Log.LogInfo($"UpdateWithValue: found via fallback, signature: {_cachedUpdateWithValue}");
                _loggedUpdateWithValue = true;
            }
            else if (!_loggedUpdateWithValue)
            {
                Log.LogInfo($"UpdateWithValue: found, signature: {_cachedUpdateWithValue}");
                _loggedUpdateWithValue = true;
            }
            return _cachedUpdateWithValue;
        }
        // PushPlayerAction calls PlayerAction.UpdateWithValue(value, tick, dt)
        // on the named PlayerAction field of the given CharacterActions.
        // Mono 2.x: never compare MethodInfo/FieldInfo with != — use (object)x == null.
        private static void PushPlayerAction(object actions, string fieldName, float value)
        {
            if ((object)actions == null) return;
            var actionsType = actions.GetType();
            string cacheKey = actionsType.FullName + "|" + fieldName;
            FieldInfo f;
            if (!_pushFieldCache.TryGetValue(cacheKey, out f))
            {
                f = AccessTools.Field(actionsType, fieldName);
                _pushFieldCache[cacheKey] = f;
            }
            if ((object)f == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field not found on type {actionsType}"); _loggedPushPath = true; }
                return;
            }
            var action = f.GetValue(actions);
            if ((object)action == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: field value is null"); _loggedPushPath = true; }
                return;
            }
            var m = GetUpdateWithValueMethod();
            if ((object)m == null)
            {
                if (!_loggedPushPath) { Log.LogWarning($"PushPlayerAction[{fieldName}]: UpdateWithValue method lookup failed; action type={action.GetType()}"); _loggedPushPath = true; }
                return;
            }
            try
            {
                _pushArgsBuffer[0] = value;
                _pushArgsBuffer[1] = (ulong)0;
                _pushArgsBuffer[2] = Time.deltaTime;
                m.Invoke(action, _pushArgsBuffer);
                if (!_loggedPushPath) { Log.LogInfo($"PushPlayerAction[{fieldName}]: invoke ok, value={value}"); _loggedPushPath = true; }
            }
            catch (Exception e)
            {
                if (!_loggedPushPath) { Log.LogError($"PushPlayerAction[{fieldName}] invoke threw: {e}"); _loggedPushPath = true; }
            }
        }

        private void WriteInputsToRigs()
        {
            if (SlotToRig.Count == 0) return;
            if (!_loggedFirstWrite) { Log.LogInfo($"WriteInputsToRigs called for first time. SlotToRig.Count={SlotToRig.Count} SlotInputs.Count={SlotInputs.Count}"); _loggedFirstWrite = true; }
            try
            {
                foreach (var kv in SlotToRig)
                {
                    int slot = kv.Key;
                    GameObject rig = kv.Value;
                    if ((object)rig == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: rig null"); _loggedFirstWriteIter = true; } continue; }
                    if (!SlotInputs.TryGetValue(slot, out var input)) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: SlotInputs miss"); _loggedFirstWriteIter = true; } continue; }

                    var ctrlType = CachedType("Controller");
                    if ((object)ctrlType == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no Controller type"); _loggedFirstWriteIter = true; } continue; }
                    var ctrl = rig.GetComponent(ctrlType);
                    if ((object)ctrl == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no Controller on rig"); _loggedFirstWriteIter = true; } continue; }
                    var actionsField = AccessTools.Field(ctrlType, "mPlayerActions");
                    if ((object)actionsField == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: no mPlayerActions field"); _loggedFirstWriteIter = true; } continue; }
                    var actions = actionsField.GetValue(ctrl);
                    if ((object)actions == null) { if (!_loggedFirstWriteIter) { Log.LogWarning($"WriteInputs iter: mPlayerActions is null"); _loggedFirstWriteIter = true; } continue; }

                    if (!_loggedFirstWriteIter) { Log.LogInfo($"WriteInputs iter: REACHED PushPlayerAction, actions type={actions.GetType().FullName}, stick=({input.StickX},{input.StickY})"); _loggedFirstWriteIter = true; }

                    // Feed the underlying L/R/U/D PlayerActions — that's
                    // what CharacterActions.Movement (a PlayerTwoAxisAction)
                    // computes its X/Y from. Setting Movement.thisValue
                    // directly gets overwritten next frame by
                    // PlayerTwoAxisAction.Update reading L/R/U/D.
                    PushPlayerAction(actions, "Left",  Mathf.Max(0f, -input.StickX));
                    PushPlayerAction(actions, "Right", Mathf.Max(0f,  input.StickX));
                    PushPlayerAction(actions, "Up",    Mathf.Max(0f,  input.StickY));
                    PushPlayerAction(actions, "Down",  Mathf.Max(0f, -input.StickY));

                    PushPlayerAction(actions, "AimLeft",  Mathf.Max(0f, -input.AimX));
                    PushPlayerAction(actions, "AimRight", Mathf.Max(0f,  input.AimX));
                    PushPlayerAction(actions, "AimUp",    Mathf.Max(0f,  input.AimY));
                    PushPlayerAction(actions, "AimDown",  Mathf.Max(0f, -input.AimY));

                    PushPlayerAction(actions, "Jump",         (input.Buttons & 0x01) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Jump2",        (input.Buttons & 0x01) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "PunchOrFire",  (input.Buttons & 0x02) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Block",        (input.Buttons & 0x04) != 0 ? 1f : 0f);
                    PushPlayerAction(actions, "Throw",        (input.Buttons & 0x08) != 0 ? 1f : 0f);
                }
            }
            catch (Exception e)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _writeInputsErrLogAt >= 5f)
                {
                    _writeInputsErrLogAt = now;
                    Log.LogWarning($"WriteInputsToRigs: {e.Message}");
                }
            }
        }
    }
}
