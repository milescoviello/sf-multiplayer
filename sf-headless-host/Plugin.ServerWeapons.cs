using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFHeadlessHost
{
    // The server keeps its own copy of every weapon it spawns or drops, as the
    // stock host does ("gun spawns don't work fully correctly").
    //
    // Stock: the host sends WeaponSpawned / WeaponDropped to everyone including
    // itself, so the host creates the weapon too, its NetworkSyncableObject has
    // control, and every client's copy follows the host's position (ObjectUpdate).
    // A pickup is granted only for a weapon the host has, and destroys the
    // host's copy. The headless server never receives its own broadcast, so it
    // had no weapons at all: every client simulated each dropped gun on its own
    // (a gun that bounced off a crate or a player landed in different places on
    // different screens) and nothing kept them together.
    //
    // Now the server runs the same OnWeaponSpawned / OnWeaponDropped on the body
    // it broadcasts; its copy's ObjectUpdates go out through the normal host
    // broadcast path, which clients already apply to weapons (they leave weapons
    // out of the snapshot apply for this). A pickup destroys the server's copy;
    // a map change clears them with the rest of the old map.
    // Thrown weapons stay with the thrower's own simulation (unchanged): a throw
    // is a hit attempt, and pulling it toward a server copy that starts one ping
    // later would cost the thrower hits.
    //
    // OFF by default (SF_SERVER_WEAPONS=1 turns it on). Measured 11-11 on Desert8
    // (a map built of 162 crates, mass 500, the guns 60): with the server copies
    // on, the server's crate stack came apart within 8 s of the round start (13
    // crates in the void, mean move 4.3 u; every client follows the server's
    // crates); off, it held (0 in the void, 0.26 u). Our server's crate physics
    // (tuned for stability) don't take a gun landing on them the way stock's
    // presumably do. Until that's fixed, clients keep simulating dropped guns on
    // their own, as before. Copies, pickups and the map-change cleanup below all
    // follow this switch.
    public partial class Plugin
    {
        private static readonly bool ServerWeaponCopies = Environment.GetEnvironmentVariable("SF_SERVER_WEAPONS") == "1";
        private static MethodInfo _mmOnWeaponSpawnedM, _mmOnWeaponDroppedM;
        private static FieldInfo _mmSpawnedWeaponsF, _mmDefaultPickupF, _wpUnEndingF;
        private static int _srvWeaponCopies, _srvWeaponErrors, _srvWeaponsRemoved;
        // Sync ids of this round's server copies, and how many ObjectUpdates went out for them.
        private static readonly System.Collections.Generic.HashSet<ushort> _srvWeaponSyncIds = new System.Collections.Generic.HashSet<ushort>();
        private static int _srvWeaponUpdates, _srvWeaponCopiesRound;
        internal static void NoteHostObjectUpdate(byte[] data)
        {
            if (data == null || data.Length < 2 || _srvWeaponSyncIds.Count == 0) return;
            if (_srvWeaponSyncIds.Contains((ushort)(data[0] | (data[1] << 8)))) _srvWeaponUpdates++;
        }

        private static void EnsureServerWeaponReflection(object mm)
        {
            if ((object)_mmSpawnedWeaponsF != null || (object)mm == null) return;
            var t = mm.GetType();
            _mmOnWeaponSpawnedM = AccessTools.Method(t, "OnWeaponSpawned", new[] { typeof(byte[]) });
            _mmOnWeaponDroppedM = AccessTools.Method(t, "OnWeaponDropped", new[] { typeof(byte[]) });
            _mmSpawnedWeaponsF = AccessTools.Field(t, "mSpawnedWeapons");
            _mmDefaultPickupF = AccessTools.Field(t, "mDefaultWeaponPickUp");
            var wp = CachedType("WeaponPickUp");
            if ((object)wp != null) _wpUnEndingF = AccessTools.Field(wp, "unEnding");
        }

        // body = exactly what went to the clients (WeaponSpawned or WeaponDropped).
        internal void KeepServerWeaponCopy(bool dropped, byte[] body)
        {
            if (!ServerWeaponCopies || body == null) return;
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                EnsureServerWeaponReflection(mm);
                var m = dropped ? _mmOnWeaponDroppedM : _mmOnWeaponSpawnedM;
                if ((object)m == null) return;
                // mConnectedClients is null on the headless server; OnWeaponSpawned's
                // last step (PingAllUsers, before the present wrapping) walks it.
                // An empty list stands in for the call, as for bullet hits.
                HitObject_ClientsPrefix();
                try { m.Invoke(mm, new object[] { body }); }
                finally { HitObject_ClientsFinalizer(null); }
                // sync id: WeaponSpawned [u8 id][i8 y][i8 z][u16 spawnId][u16 syncId][u8 present];
                // WeaponDropped [request body][u16 spawnId][u16 syncId] (last two bytes)
                int so = dropped ? body.Length - 2 : 5;
                if (so >= 0 && so + 1 < body.Length) _srvWeaponSyncIds.Add((ushort)(body[so] | (body[so + 1] << 8)));
                _srvWeaponCopiesRound++;
                if (_srvWeaponCopies++ < 3 || _srvWeaponCopies % 50 == 0)
                    Log.LogInfo($"[weapons] server copy of a {(dropped ? "dropped" : "spawned")} weapon made (#{_srvWeaponCopies}); clients follow its position.");
            }
            catch (Exception e)
            {
                var ie = e.InnerException ?? e;
                if (_srvWeaponErrors++ < 5) Log.LogWarning($"[weapons] server copy of a {(dropped ? "dropped" : "spawned")} weapon ({body.Length} bytes): {ie.GetType().Name}: {ie.Message}\n{ie.StackTrace}");
            }
        }

        // A granted pickup: the weapon leaves the ground on every client; the
        // server's copy goes too (stock OnWeaponWasPickedUp destroys it).
        internal void RemoveServerWeaponCopy(ushort weaponNetId)
        {
            if (!ServerWeaponCopies) return;
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                EnsureServerWeaponReflection(mm);
                var dict = (object)_mmSpawnedWeaponsF != null ? _mmSpawnedWeaponsF.GetValue(mm) as IDictionary : null;
                if (dict == null || !dict.Contains(weaponNetId)) return;
                var c = dict[weaponNetId] as Component;
                object def = (object)_mmDefaultPickupF != null ? _mmDefaultPickupF.GetValue(mm) : null;
                if (c == null || ReferenceEquals(c, def)) return;
                if ((object)_wpUnEndingF != null && (bool)_wpUnEndingF.GetValue(c)) return;   // stock keeps unending ones
                UnityEngine.Object.Destroy(c.gameObject);
                dict.Remove(weaponNetId);
                _srvWeaponsRemoved++;
            }
            catch (Exception e)
            {
                if (_srvWeaponErrors++ < 5) Log.LogWarning($"[weapons] server pickup: {(e.InnerException ?? e).Message}");
            }
        }

        // Map change: the old round's spawned and dropped weapons go (they live
        // outside the map scene, so its unload doesn't take them). Called before
        // the stock ClearWeapons empties the table.
        internal void DestroyServerWeaponCopies()
        {
            if (!ServerWeaponCopies) return;
            try
            {
                var mm = GetMultiplayerManagerInstance();
                if ((object)mm == null) return;
                EnsureServerWeaponReflection(mm);
                var dict = (object)_mmSpawnedWeaponsF != null ? _mmSpawnedWeaponsF.GetValue(mm) as IDictionary : null;
                if (_srvWeaponCopiesRound > 0)
                    Log.LogInfo($"[weapons] last round: {_srvWeaponCopiesRound} server weapon copies, {_srvWeaponUpdates} position updates sent for them.");
                _srvWeaponCopiesRound = 0; _srvWeaponUpdates = 0; _srvWeaponSyncIds.Clear();
                if (dict == null || dict.Count == 0) return;
                object def = (object)_mmDefaultPickupF != null ? _mmDefaultPickupF.GetValue(mm) : null;
                int n = 0;
                foreach (DictionaryEntry e in dict)
                {
                    var c = e.Value as Component;
                    if (c == null || ReferenceEquals(c, def)) continue;
                    UnityEngine.Object.Destroy(c.gameObject);
                    n++;
                }
                if (n > 0 && _srvWeaponCopies < 200) Log.LogInfo($"[weapons] map change: removed {n} server weapon copies of the old round.");
            }
            catch (Exception e)
            {
                if (_srvWeaponErrors++ < 5) Log.LogWarning($"[weapons] map change: {(e.InnerException ?? e).Message}");
            }
        }
    }
}
