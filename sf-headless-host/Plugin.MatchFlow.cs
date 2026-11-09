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

        private int CountInitializedSfClients()
        {
            int n = 0;
            foreach (var kv in _sfClients)
                if (kv.Value.Initialized) n++;
            return n;
        }

        // ================================================================
        // Round / lobby rules — mirrors the stock host (GameManager.KillPlayer):
        //   * a round ends when <= 1 participant is still alive; the survivor's
        //     slot (NetworkSpawnID) is the MapChange winner, 255 = nobody;
        //   * in the lobby room ("kill your friends to start") the match starts
        //     the same way, but only with >= 2 players in the lobby.
        // Deaths are client-authoritative exactly like stock SF: the dying
        // player's own client reports it (PlayerTookDamage carrying the 666.666
        // killing-blow marker, or PlayerFallOut) on ITS event channel
        // (NetworkPlayer: update ch = spawnID*2+2, event ch = +1), and keeps
        // re-sending it while dead. The server's own authoritative rigs are a
        // second simulation that can diverge, so they never end a round.
        // ================================================================
        private readonly HashSet<int> _deadThisRound = new HashSet<int>();
        private byte _pendingRoundWinner = 255;
        // StartMatch went out for the current map → the round is live. Death
        // reports before that are late packets from the previous round (sent
        // just before the client processed MapChange) and must not count.
        private bool _startMatchSentThisRound;
        private int _roundParticipantsAtStart;
        // Ready-up gate: StartMatch goes out once every participant has sent
        // ClientReadyUp (sent by the client after its map load + intro), never
        // before _startMatchEarliestAt, and at the latest at _startMatchDeadline.
        private readonly HashSet<int> _readySlots = new HashSet<int>();
        private float _startMatchEarliestAt = -1f;
        private float _startMatchDeadline = -1f;
        private const float StartMatchMaxWaitSec = 5f;   // past the earliest time
        private int _deathReportsRejected;

        private void ArmStartMatchAfterLoad(float earliestDelaySec)
        {
            float now = Time.realtimeSinceStartup;
            _readySlots.Clear();
            _startMatchSentThisRound = false;
            _startMatchEarliestAt = now + earliestDelaySec;
            _startMatchDeadline = _startMatchEarliestAt + StartMatchMaxWaitSec;
        }

        private bool AllParticipantsReady()
        {
            int n = 0;
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                if (!c.Initialized || c.Slot < 0) continue;
                n++;
                if (!_readySlots.Contains(c.Slot)) return false;
            }
            return n > 0;
        }

        // Called every frame from the Running loop.
        private void TickStartMatchGate()
        {
            if (_startMatchEarliestAt < 0f) return;
            float now = Time.realtimeSinceStartup;
            if (now < _startMatchEarliestAt) return;
            bool allReady = AllParticipantsReady();
            if (!allReady && now < _startMatchDeadline) return;
            _startMatchEarliestAt = -1f;
            _startMatchDeadline = -1f;
            Log.LogInfo($"[SF] StartMatch gate open ({(allReady ? "all players loaded" : "ready-up deadline")}; ready={_readySlots.Count}/{CountInitializedSfClients()}).");
            BroadcastStartMatch();
        }

        private void OnClientReadyUp(SfClient cli)
        {
            if (!cli.Initialized || cli.Slot < 0) return;
            _readySlots.Add(cli.Slot);
            // Stock host: a ReadyUp that arrives while the fight is already on
            // (late joiner / late loader) gets StartMatch sent to that user only.
            if (_matchStarted && _startMatchSentThisRound)
                SendSfPacket(cli.Addr, PktStartMatch, new byte[0], 0, 0);
        }

        // A client reported its own death. `channel` is the packet's channel.
        private void OnClientReportedDeath(SfClient cli, byte channel, string reason)
        {
            if (cli == null || !cli.Initialized || cli.Slot < 0) return;
            int victim = channel >= 2 ? (channel - 2) / 2 : -1;
            if (victim != cli.Slot)
            {
                // A client may only report its OWN death (its own event channel).
                if (_deathReportsRejected++ < 5 || _deathReportsRejected % 50 == 0)
                    Log.LogWarning($"[DEATH] ignored {reason} from slot {cli.Slot} on channel {channel} (channel slot {victim}) #{_deathReportsRejected}");
                return;
            }
            if (_matchStarted && !_startMatchSentThisRound) return;   // previous round's late packet
            if (!_deadThisRound.Add(victim)) return;                   // dead-heartbeat repeat
            Log.LogInfo($"[DEATH] slot {victim} died ({reason}).");
            if (_matchStarted) CheckRoundEnd(reason);
            else CheckLobbyStart(reason);
        }

        private void CountAlive(out int participants, out int alive, out int lastAlive)
        {
            participants = 0; alive = 0; lastAlive = -1;
            var seen = new HashSet<int>();
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                if (!c.Initialized || c.Slot < 0 || !seen.Add(c.Slot)) continue;
                participants++;
                if (!_deadThisRound.Contains(c.Slot)) { alive++; lastAlive = c.Slot; }
            }
        }

        internal void CheckRoundEnd(string reason)
        {
            if (!_matchStarted || !_startMatchSentThisRound || _pendingRoundAdvanceAt >= 0f) return;
            CountAlive(out int participants, out int alive, out int lastAlive);
            // Started as a real match (2+): last one standing wins (this also
            // covers the opponent disconnecting). Solo QA lobby: your own death
            // moves to the next map.
            bool over = _roundParticipantsAtStart >= 2 ? alive <= 1 : alive == 0;
            if (!over) return;
            _pendingRoundWinner = (_roundParticipantsAtStart >= 2 && alive == 1) ? (byte)lastAlive : (byte)255;
            Log.LogInfo($"[SF] Round over ({reason}): alive={alive}/{participants} winner={(_pendingRoundWinner == 255 ? "none" : "slot " + _pendingRoundWinner)}.");
            TryScheduleRoundAdvance(reason);
        }

        private void CheckLobbyStart(string reason)
        {
            CountAlive(out int participants, out int alive, out int _);
            if (participants < 2 || alive > 1) return;   // stock: needs 2+ in the lobby
            FireMatchStart($"lobby: last player standing ({reason})");
        }

        /// <summary>One connected player in an active match — solo physics/map QA.</summary>
        private bool IsSoloTestLobby() => _matchStarted && CountInitializedSfClients() == 1;

        private void TickPeriodicWeaponRearm()
        {
            if (!_matchStarted) return;
            float now = Time.realtimeSinceStartup;
            if (_lastPeriodicRearmAt < 0f) _lastPeriodicRearmAt = now;
            if (now - _lastPeriodicRearmAt < 4f) return;
            _lastPeriodicRearmAt = now;
            try
            {
                var gmType = AccessTools.TypeByName("GameManager");
                if ((object)gmType == null) return;
                object gmInst = null;
                var ig = AccessTools.PropertyGetter(gmType, "Instance");
                if ((object)ig != null) gmInst = ig.Invoke(null, null);
                if ((object)gmInst == null) return;
                var inFightF = AccessTools.Field(gmType, "inFight");
                bool inFight = (object)inFightF != null && (bool)inFightF.GetValue(gmInst);
                if (inFight) return;
                Log.LogInfo("[P6.5] Periodic rearm: inFight was false mid-match.");
                RearmOracleCombatLoop("periodic");
            }
            catch { }
        }

        /// <summary>Clears post-map gate so death can schedule round advance immediately.</summary>
        internal void ClearRoundAdvanceBlockedGate(string reason)
        {
            _roundAdvanceBlockedUntil = Time.realtimeSinceStartup;
            Log.LogInfo($"[DEATH] Round advance gate cleared ({reason}).");
        }

        // The server's own rig died (HealthHandler.Die on the oracle). Logged
        // only — round results come from the players' clients (see above).
        internal void OnOraclePlayerDied(object healthHandler, string reason)
        {
            if (Verbose) Log.LogDebug($"[DEATH] server rig died ({reason}) — informational, rounds follow client reports.");
        }

        private void TryScheduleRoundAdvance(string reason)
        {
            if (_pendingRoundAdvanceAt >= 0f) return;
            float now = Time.realtimeSinceStartup;
            if (_roundAdvanceBlockedUntil > 0f && now < _roundAdvanceBlockedUntil)
            {
                Log.LogInfo($"[SF] Round advance ignored ({reason}): map grace {(_roundAdvanceBlockedUntil - now):0.0}s left.");
                return;
            }
            if (IsOracleMapLoadInProgress())
            {
                _roundAdvanceQueuedAfterMapLoad = true;
                Log.LogInfo($"[SF] Round advance queued ({reason}): map load in progress.");
                return;
            }
            if (!_matchStarted)
            {
                Log.LogDebug($"[SF] Round advance ignored ({reason}): match not started.");
                return;
            }
            _pendingRoundAdvanceAt = now + RoundEndDelaySec;
            Log.LogInfo($"[SF] Round advance scheduled ({reason}) in {RoundEndDelaySec:0.0}s — clients={CountInitializedSfClients()} soloTest={IsSoloTestLobby()}");
        }

        internal void FlushQueuedRoundAdvanceAfterMapLoad(string reason)
        {
            if (!_roundAdvanceQueuedAfterMapLoad || _pendingRoundAdvanceAt >= 0f) return;
            _roundAdvanceQueuedAfterMapLoad = false;
            if (!_matchStarted) return;
            _pendingRoundAdvanceAt = Time.realtimeSinceStartup + RoundEndDelaySec;
            Log.LogInfo($"[SF] Round advance scheduled ({reason}) after map load in {RoundEndDelaySec:0.0}s.");
        }

        private static bool TryGetRigIsDead(GameObject rig)
        {
            if ((object)rig == null) return false;
            try
            {
                var ctrlType = AccessTools.TypeByName("Controller");
                if ((object)ctrlType == null) return false;
                var ctrl = rig.GetComponent(ctrlType);
                if ((object)ctrl == null) return false;
                var infoF = AccessTools.Field(ctrlType, "info");
                if ((object)infoF == null) return false;
                var infoVal = infoF.GetValue(ctrl);
                if ((object)infoVal == null) return false;
                var deadF = AccessTools.Field(infoVal.GetType(), "isDead");
                if ((object)deadF != null) return (bool)deadF.GetValue(infoVal);
                var deadP = AccessTools.Property(infoVal.GetType(), "isDead");
                if ((object)deadP != null) return (bool)deadP.GetValue(infoVal, null);
            }
            catch { }
            return false;
        }

        private void TickAuthRigDeathCheck()
        {
            if (SlotToRig.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now - _authDeathCheckAt < 0.25f) return;
            _authDeathCheckAt = now;
            foreach (var kv in SlotToRig)
            {
                if (_deathSlotsHandled.Contains(kv.Key)) continue;
                if ((object)kv.Value == null) continue;
                if (!TryGetRigIsDead(kv.Value)) continue;
                _deathSlotsHandled.Add(kv.Key);
                // Informational: the server rig is a second sim that can diverge
                // from the player's own client, so it never decides a round.
                Log.LogInfo($"[DEATH] server rig slot {kv.Key} isDead (informational).");
            }
        }

        private void ResetDeathTrackingForNewRound()
        {
            _deathSlotsHandled.Clear();
            _deadThisRound.Clear();
            _pendingRoundWinner = 255;
            _roundAdvanceQueuedAfterMapLoad = false;
        }
        // Exposed so a chat command / log can show what's excluded.
        internal static string ExcludedMapsInfo()
        {
            string ex = System.Environment.GetEnvironmentVariable("SF_EXCLUDE_MAPS");
            return string.IsNullOrEmpty(ex) ? "102 (stats)" : ("102 (stats), " + ex);
        }

        private void AdvanceRound()
        {
            if (_allLandfallMaps.Length == 0)
            {
                // Misconfig guard: SF_EXCLUDE_MAPS can empty the pool, and
                // _mapRng.Next(0)==0 → _allLandfallMaps[0] would throw (caught by
                // Update, but it wedges round progression). Bail cleanly instead.
                Log.LogError("[SF] AdvanceRound: no playable Landfall maps (SF_EXCLUDE_MAPS too broad) — staying on the current scene.");
                return;
            }
            byte winner = _pendingRoundWinner;
            ResetDeathTrackingForNewRound();
            AcResetRound();   // (B2) reset behavioral-AC accumulators at the real round boundary
            _roundCounter++;
            // Pick a random scene we haven't visited in the last few rounds.
            int nextScene = _allLandfallMaps[_mapRng.Next(_allLandfallMaps.Length)];
            for (int attempt = 0; attempt < 8 && _recentMaps.Contains(nextScene); attempt++)
                nextScene = _allLandfallMaps[_mapRng.Next(_allLandfallMaps.Length)];
            _recentMaps.Enqueue(nextScene);
            while (_recentMaps.Count > _recentMapsAvoidWindow) _recentMaps.Dequeue();
            _currentSceneIndex = nextScene;
            _roundAdvanceBlockedUntil = Time.realtimeSinceStartup + RoundMinPlaySec;
            bool solo = IsSoloTestLobby();
            Log.LogInfo($"[SF] Round advance #{_roundCounter}: MapChange → scene {nextScene} (winner={winner}, soloTest={solo})");
            // ChangeMap body: [byte winnerIndex (slot, 255 = none)][byte mapType=0 (Landfall)][int32 sceneIndex LE].
            // Clients use the winner for the crown, win text and win counter
            // (GameManager.NetworkAllPlayersDiedButOne → StartMatch → LastWinner).
            byte[] body = new byte[1 + 1 + 4];
            body[0] = winner;
            body[1] = 0;
            WriteU32LE(body, 2, (uint)nextScene);
            BroadcastSfPacket(PktMapChange, body, 0, 0);
            // Stock host: StartMatch once every client has re-readied (each
            // sends ClientReadyUp after its map load + intro). Never earlier
            // than SF_NEXT_MATCH_DELAY (the oracle's own reload), never later
            // than StartMatchMaxWaitSec past that.
            ArmStartMatchAfterLoad(NextMatchDelaySec);
            _pendingRearmCombatAt = -1f;
            _oracleCountDownAt = -1f;
            _oracleCountDownFired = false;
            ResetOracleStateForRoundAdvance();
            ClearMapDataObjectsOnOracle();
            _cachedGroundWeaponsBody = null;
            _groundWeaponsEntryCount = 0;
            _skyWeaponSpawnCount = 0;
            _oracleNextSkyWeaponAt = -1f;
            ScheduleOracleReloadCurrentMap("AdvanceRound");
            // Reset Spawned flags so next ClientRequestingToSpawn is honored.
            foreach (var kv in _sfClients) kv.Value.Spawned = false;
        }

        /// <summary>Let the next round re-register map sync, NSOs, and auth rigs on the new scene.</summary>
        private void ResetOracleStateForRoundAdvance()
        {
            _authSpawnDone = false;
            _authSpawnAt = -1f;
            _nsoInventoryDone = false;
            _nsoInventoryAt = -1f;
            _mapSyncObjectsRegistered = 0;
            _oraclePreCombatReadyAt = -1f;
            _oraclePreCombatSceneIndex = -1;
            // NOTE: _nsoLastBroadcastPos and _nsoLastMovedAt are NOT cleared here.
            // They're keyed by NSO ushort Index which gets reassigned per scene;
            // stale entries for retired indices are harmless because
            // CollectActiveNsoSnapshot finds live components fresh via
            // FindObjectsOfType each tick. PRIOR BEHAVIOR (clearing them) caused
            // a permanent post-round-advance lockout where nsos=0 between
            // keyframes because no per-NSO entry could ever satisfy
            // `recentlyActive` again — see Bug B in
            // notes/bug-investigations/2026-05-24_v0.3.4-session-bugs.md.
            _nsoSpawnPos.Clear();
            _nsoVoidResetCount.Clear();   // per-NSO-index void-rescue budget — reset with the map (ids get reassigned)
            _nsoByIndexCache.Clear();
            _nsoCacheLastRebuildAt = -1f;
            // Drop any still-in-flight projectiles at the round boundary. TickProjectiles
            // runs every FixedUpdate (not gated on _matchStarted) and reads live SlotToRig,
            // so a bullet/thrown-weapon mid-flight at round end would otherwise survive the
            // map change and nudge/blast the NEXT map's crates (or, with bullet-damage on,
            // phantom-hit a freshly spawned player) within its remaining lifetime.
            _projectiles.Clear();
            ClearAuthoritativeRigsForRoundAdvance();
        }

        private void ClearAuthoritativeRigsForRoundAdvance()
        {
            if (SlotToRig.Count == 0) return;
            int destroyed = 0;
            foreach (var kv in SlotToRig)
            {
                if ((object)kv.Value != null)
                {
                    UnityEngine.Object.Destroy(kv.Value);
                    destroyed++;
                }
            }
            SlotToRig.Clear();
            Log.LogInfo($"[SF] Round advance: cleared {destroyed} authoritative rig(s) for next map.");
        }

        // Consolidated match-start sequence. Called by /start chat or by
        // anything else that wants to begin a match. Idempotent — second call
        // while a match is in progress just logs and returns.
        private void FireMatchStart(string source)
        {
            if (_matchStarted)
            {
                Log.LogInfo($"[SF] FireMatchStart({source}) — already started, no-op.");
                return;
            }
            ResetDeathTrackingForNewRound();
            Log.LogInfo($"[SF] FireMatchStart({source}) — MapChange now; StartMatch to clients after load window.");
            BroadcastMapChange(_currentSceneIndex);
            ArmStartMatchAfterLoad(Mathf.Max(5f, NextMatchDelaySec + 2f));
            _matchStarted = true;
            _roundAdvanceBlockedUntil = Time.realtimeSinceStartup + RoundMinPlaySec;
            _sceneLoadRealtime = Time.realtimeSinceStartup;
            try
            {
                var mhType = AccessTools.TypeByName("MatchmakingHandler");
                if ((object)mhType != null)
                {
                    var setNetMatch = AccessTools.Method(mhType, "SetNetworkMatch");
                    if ((object)setNetMatch != null)
                    {
                        setNetMatch.Invoke(null, new object[] { true });
                        Log.LogInfo("[P6.5] MatchmakingHandler.SetNetworkMatch(true).");
                    }
                }
                ScheduleOracleReloadCurrentMap("FireMatchStart");
            }
            catch (Exception e) { Log.LogError($"[P6.5] FireMatchStart scheduling failed: {e}"); }
        }

        // MapChange body: byte winnerIndex + byte mapType + mapData.
        // For a fresh start: winnerIndex=255 (no winner), mapType=0 (Landfall),
        // mapData=i32 sceneIndex LE.
        private void BroadcastMapChange(int sceneIndex)
        {
            byte[] body = new byte[1 + 1 + 4];
            body[0] = 255;             // winnerIndex (no winner)
            body[1] = 0;               // mapType Landfall
            WriteU32LE(body, 2, (uint)sceneIndex);
            BroadcastSfPacket(PktMapChange, body, 0, 0);
            Log.LogInfo($"[SF] Broadcast MapChange → scene {sceneIndex}");
        }

        private void BroadcastStartMatch()
        {
            BroadcastSfPacket(PktStartMatch, new byte[0], 0, 0);
            _startMatchSentThisRound = true;
            _roundParticipantsAtStart = 0;
            foreach (var kv in _sfClients)
                if (kv.Value.Initialized && kv.Value.Slot >= 0) _roundParticipantsAtStart++;
            // Clear per-round spawn flag so next ClientRequestingToSpawn
            // is treated as a fresh round-start rather than a respawn.
            foreach (var kv in _sfClients) kv.Value.Spawned = false;
            Log.LogInfo("[SF] Broadcast StartMatch");
            // Weapons/combat re-arm only after PostMapLoad pre-combat grace (RunOraclePreCombatStart).
            _pendingRearmCombatAt = -1f;
        }
    }
}
