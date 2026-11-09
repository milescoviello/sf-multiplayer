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
        private static void EnsureControllerRefs()
        {
            if (!_ctrlLookupTried) { _ctrlLookupTried = true; _ctrlTypeForNp = CachedType("Controller"); }
            if ((object)_ctrlTypeForNp == null) return;
            if (!_ctrlPidLookupTried) { _ctrlPidLookupTried = true; _ctrlPlayerIdField = AccessTools.Field(_ctrlTypeForNp, "playerID"); }
            if (!_ctrlHasCtrlLookupTried) { _ctrlHasCtrlLookupTried = true; _ctrlHasControlField = AccessTools.Field(_ctrlTypeForNp, "mHasControl"); }
        }

        // Harmony prefix on DestructiblePiece.OnCollisionEnter — returns false
        // (skip stock) in two cases:
        //
        // (1) [P0-15] when the colliding rigidbody's root was snapshot-lerped
        // in the last ~150ms. Prevents the swept-lerp-into-ice-block path.
        //
        // (2) [Phase 6.21] when the colliding rigidbody is a WeaponPickUp.
        // Server-spawned shooting guns (heavy mass) fall onto chains/ice and
        // the mass>300 multiplier (10x) or mass>1000 multiplier (50x) pushes
        // the collision force past the destruction threshold → spurious
        // chain/ice break across all clients. Vanilla SF doesn't see this
        // because the host's authoritative ObjectUpdate stream pulls weapons
        // out of free-fall before they hit anything; our oracle's update
        // stream has different timing. Cheapest fix: don't let weapons
        // ever drive destruction. Player hits, throws, kicks still work
        // (those go through the player's rigid body, not the weapon's).
        internal static bool DestructibleCollisionPrefix(MonoBehaviour __instance, Collision collision)
        {
            try
            {
                if ((object)collision == null) return true;
                var rb = collision.rigidbody;
                if ((object)rb == null) return true;
                var rootT = rb.transform.root;
                if ((object)rootT == null) return true;

                // EXPLOSIVE BARRELS (eventDestruction) must ALWAYS process their
                // collision — they detonate from inside OnCollisionEnter. Our
                // suppression below is only for ice/chain pieces; without this
                // exception the prefix swallowed the barrel's hit and they never
                // exploded ("los barriles de pólvora no explotan").
                if (IsEventDestructibleTarget(__instance)) return true;

                // Ice only breaks from LOCAL player rig — not from boxes/lerped NSOs/weapons.
                if (IsIceDestructibleTarget(__instance))
                {
                    if (!IsLocalPlayerCollisionRigidbody(rb))
                        return false;
                }

                // (2) — skip if the colliding body's root has a WeaponPickUp
                // anywhere in its hierarchy. WeaponPickUp lives on the
                // weapon prefab's root in stock SF.
                if ((object)_weaponPickUpType == null)
                {
                    try { _weaponPickUpType = CachedType("WeaponPickUp"); } catch { }
                }
                if ((object)_weaponPickUpType != null && rootT.GetComponentInChildren(_weaponPickUpType, true) != null)
                {
                    _weaponSkipCount++;
                    if (_weaponSkipCount == 1 || _weaponSkipCount % 20 == 0)
                        Log.LogInfo($"[Phase 6.21] Suppressed destruction from WeaponPickUp collision (#{_weaponSkipCount}) on '{__instance?.name}'");
                    return false;
                }

                // Non-ice destructibles: block NSO/chain roots that aren't the local player.
                if (!IsIceDestructibleTarget(__instance))
                {
                    if (IsChainStyleDestructibleRoot(rootT.gameObject)
                        || IsWeaponNsoRootClient(rootT.gameObject))
                        return false;
                    if ((object)_nsoTypeForCollision == null && !_nsoTypeForCollisionTried)
                    { _nsoTypeForCollisionTried = true; _nsoTypeForCollision = CachedType("NetworkSyncableObject"); }
                    if (!IsLocalPlayerCollisionRigidbody(rb)
                        && (IsIceOnlyDestructibleRoot(rootT.gameObject)
                            || ((object)_nsoTypeForCollision != null && rootT.GetComponent(_nsoTypeForCollision) != null)))
                        return false;
                }

                float now = Time.realtimeSinceStartup;
                int id = rootT.GetInstanceID();
                if (_recentLerpAt.TryGetValue(id, out float lastLerp))
                {
                    if (now - lastLerp < LerpSuppressWindowSec)
                    {
                        // Recently teleported by snapshot apply — don't let
                        // stock code interpret this as a player-driven impact.
                        return false; // skip stock OnCollisionEnter
                    }
                }

                // Opportunistic cleanup: prune stale entries every ~100 calls.
                if ((++_destructibleGuardCallCount % 100) == 0)
                {
                    var staleKeys = new List<int>();
                    foreach (var kv in _recentLerpAt)
                        if (now - kv.Value > 2.0f) staleKeys.Add(kv.Key);
                    foreach (var k in staleKeys) _recentLerpAt.Remove(k);
                }
            }
            catch { /* let stock run on any error */ }
            return true;
        }

        // ===== Music crash guard =====
        // SF's MusicHandler.PlayNext() calls AudioSource.Play(), which crashes
        // natively in this Unity 5.6 build when it streams the next track
        // (observed: hard crash, stack MusicHandler.Update→PlayNext→AudioSource.Play).
        // We neutralize the music system so the game can't crash there. Music is
        // cosmetic; stability wins.
        private void InstallMusicCrashGuard()
        {
            try
            {
                var mhType = CachedType("MusicHandler");
                if ((object)mhType == null) { Log.LogInfo("[music-guard] MusicHandler not found — skip."); return; }
                var harmony = new Harmony(PluginGuid + ".music-guard");
                int n = 0;
                var playNext = AccessTools.Method(mhType, "PlayNext");
                if ((object)playNext != null)
                {
                    harmony.Patch(playNext, prefix: new HarmonyMethod(typeof(Plugin), nameof(MusicHandler_Skip_Prefix)));
                    n++;
                }
                var tryStart = AccessTools.Method(mhType, "TryStartMusic");
                if ((object)tryStart != null)
                {
                    harmony.Patch(tryStart, prefix: new HarmonyMethod(typeof(Plugin), nameof(MusicHandler_Skip_Prefix)));
                    n++;
                }
                Log.LogInfo($"[music-guard] Disabled MusicHandler audio ({n} method(s)) to prevent native AudioSource.Play crash.");
            }
            catch (Exception e) { Log.LogWarning($"[music-guard] install failed: {e.Message}"); }
        }

        // Returns false → original method body is skipped entirely.
        internal static bool MusicHandler_Skip_Prefix() { return false; }


        // Phase 6.17 v0.1 — Harmony postfix on Weapon.ActuallyShoot.
        // Fires after the local Shoot ran. We capture the muzzle position +
        // forward direction from the Weapon instance and send a
        // PktClientFireWeapon (msgType 41) to the oracle. Server simulates
        // the projectile + broadcasts to all clients in WorldStateSnapshot.
        //
        // Only sends for the LOCAL player's weapon (HasControl=true on the
        // Controller holding this weapon) — remote players' Shoot postfix
        // also fires when their player rig replays the action, and we don't
        // want to double-emit.
        private static void WeaponShootPostfix(object __instance,
            bool networkForce,
            Vector3 shootVectorOverride,
            Vector3 shootPositionOverride)
        {
            try
            {
                if (Instance == null || Instance._socket == null || Instance._serverEp == null) return;

                var weaponComp = __instance as Component;
                if ((object)weaponComp == null) return;

                // Find owning Controller — Weapon is a child of the player rig.
                EnsureControllerRefs();
                if ((object)_ctrlTypeForNp == null) return;
                var ctrl = weaponComp.GetComponentInParent(_ctrlTypeForNp);
                if ((object)ctrl == null) return;
                if ((object)_ctrlHasControlField != null && !(bool)_ctrlHasControlField.GetValue(ctrl)) return;  // not the local player
                byte slot = 0;
                if ((object)_ctrlPlayerIdField != null) slot = (byte)(int)_ctrlPlayerIdField.GetValue(ctrl);

                // Origin = shootPositionOverride if set, else weapon's shootPosition.position
                Vector3 origin = networkForce ? shootPositionOverride : weaponComp.transform.position;
                Vector3 dir    = networkForce ? shootVectorOverride   : weaponComp.transform.forward;
                if (dir.sqrMagnitude < 0.001f) return;
                dir.Normalize();

                Instance.SendFireWeaponPacket(slot, 0 /*weaponType placeholder*/, origin, dir, 0f);
            }
            catch (Exception e) { Log.LogWarning($"WeaponShootPostfix: {e.Message}"); }
        }
    }
}
