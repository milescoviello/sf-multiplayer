using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFClientRecon
{
    public partial class Plugin
    {
        private void AnnounceLocalSlot(string source)
        {
            if (_localSlot == _lastAnnouncedSlot) return;
            _lastAnnouncedSlot = _localSlot;
            Log.LogInfo($"[P6.11] Discovered localSlot={_localSlot} ({source}).");
        }

        // Until we have a slot (menu, lobby list, before spawning) every lookup
        // scanned the scene (FindObjectsOfType), and it ran every frame: about 7 ms
        // a frame on a fast PC, 12-15 ms on a 2009 Core 2 Duo. A miss is now
        // remembered: 0.25 s for the first 3 s after a reset (joining a lobby, a
        // new map), so the slot is picked up right after spawning, then 0.5 s,
        // and 2 s once nothing has turned up for 15 s (sitting in the menu).
        private float _slotMissUntil = -1f, _slotResetAt = -100f;

        private int FindLocalSlot()
        {
            if (_localSlot >= 0) return _localSlot;
            float now = Time.realtimeSinceStartup;
            if (now < _slotMissUntil) return -1;
            int found = FindLocalSlotScan();
            if (found < 0)
            {
                float since = now - _slotResetAt;
                _slotMissUntil = now + (since < 3f ? 0.25f : since < 15f ? 0.5f : 2f);
            }
            return found;
        }

        // Forget the slot and look again soon (lobby join or switch, map change).
        internal void ResetLocalSlot()
        {
            _localSlot = -1;
            _slotMissUntil = -1f;
            _slotResetAt = Time.realtimeSinceStartup;
        }

        private int FindLocalSlotScan()
        {
            try
            {
                // v0.6.0 — slot discovery, most-authoritative first. History:
                // mHasControl scanning is always-false in server-auth mode; the
                // first-NetworkPlayer fallback is order-dependent (every client
                // claimed slot 0 → the server's _slotV26Endpoint[0] flip-flopped
                // 40×/s and slot 1's snapshot stream went to the :1339 default
                // port — seen on the wire 2026-06-11); and
                // MultiplayerManager.mLocalPlayerIndex is NOT populated by the
                // patched DLL in oracle mode (both clients read the default 0 —
                // the decompile shows the stock path only).
                //
                // (1) The local player's NetworkPlayer carries
                // mHasLocalControl=true in online matches (the field Phase-5
                // PlayerSync used successfully); its Controller.playerID is the
                // server-assigned slot.
                EnsureControllerRefs();
                if (!_npLocalCtlLookupTried)
                {
                    _npLocalCtlLookupTried = true;
                    var npT = CachedType("NetworkPlayer");
                    if ((object)npT != null)
                        _npHasLocalControlField = AccessTools.Field(npT, "mHasLocalControl");
                }
                if ((object)_npHasLocalControlField != null)
                {
                    var npTypeScan = CachedType("NetworkPlayer");
                    var npsScan = (object)npTypeScan != null ? UnityEngine.Object.FindObjectsOfType(npTypeScan) : null;
                    if (npsScan != null)
                    {
                        foreach (var np in npsScan)
                        {
                            try
                            {
                                if (!(bool)_npHasLocalControlField.GetValue(np)) continue;
                            }
                            catch { continue; }
                            int slotNp;
                            if (TryGetPlayerSlotFromNetworkPlayer(np, out slotNp))
                            {
                                _localSlot = slotNp;
                                AnnounceLocalSlot("NetworkPlayer.mHasLocalControl");
                                return _localSlot;
                            }
                        }
                    }
                }
                // (2) mLocalPlayerIndex — trust only a NONZERO value (zero is
                // indistinguishable from the never-set default here).
                if (!_mmSlotLookupTried)
                {
                    _mmSlotLookupTried = true;
                    _mmTypeForSlot = CachedType("MultiplayerManager");
                    if ((object)_mmTypeForSlot != null)
                        _mmLocalPlayerIndexField = AccessTools.Field(_mmTypeForSlot, "mLocalPlayerIndex");
                }
                if ((object)_mmLocalPlayerIndexField != null)
                {
                    var mm = UnityEngine.Object.FindObjectOfType(_mmTypeForSlot);
                    if (RefOk(mm))
                    {
                        int idx = (byte)_mmLocalPlayerIndexField.GetValue(mm);
                        if (idx > 0)
                        {
                            _localSlot = idx;
                            AnnounceLocalSlot("MultiplayerManager.mLocalPlayerIndex");
                            return _localSlot;
                        }
                    }
                }
                // (3) Controller.mHasControl — OFFLINE/local play only. In
                // oracle mode this path is never right: the server never
                // grants mHasControl, but the MENU scene stays additively
                // loaded during matches and its local Controller (playerID 0,
                // mHasControl=true) is always findable — it produced the
                // pre-connect slot-0 window of 2026-06-11.
                if (!_oracleConnectMode && (object)_ctrlTypeForNp != null)
                {
                    var ctrls = UnityEngine.Object.FindObjectsOfType(_ctrlTypeForNp);
                    if (ctrls != null && (object)_ctrlHasControlField != null && (object)_ctrlPlayerIdField != null)
                    {
                        foreach (var c in ctrls)
                        {
                            if (!(bool)_ctrlHasControlField.GetValue(c)) continue;
                            _localSlot = (int)_ctrlPlayerIdField.GetValue(c);
                            AnnounceLocalSlot("Controller, offline mode");
                            return _localSlot;
                        }
                    }
                }
                // NOTE: the old "first NetworkPlayer found" fallback is GONE on
                // purpose — it's order-dependent and was the source of every
                // client claiming slot 0. Returning -1 (inputs wait for spawn)
                // is strictly better than caching a wrong slot.
            }
            catch (Exception e) { Log.LogWarning($"FindLocalSlot: {e.Message}"); }
            return -1;
        }
    }
}
