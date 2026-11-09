using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SFProbe
{
    // Logging-only probe for measuring the STOCK game (no server, no mods).
    // Active only with SFPROBE=1 in the environment; changes nothing:
    // Harmony postfixes that read values, and per-frame sampling. Writes
    // SFProbe.log next to the game (tab-separated: time, kind, details).
    //   round:  KillPlayer, AllButOnePlayersDied, StartMatch, StartCountDown,
    //           inFight on/off, scene loads
    //   props:  every non-player rigidbody's velocity jump > 1 m/s in one
    //           physics step, with mass and the latest cause seen within
    //           0.1 s (bullet hit with its force, punch, explosion, player
    //           contact, or none)
    //   hits:   ProjectileCollision.HitObject (target mass, weapon force,
    //           damage), PunchForce contacts on props, Explosion.Explode
    //   weapons: SpawnRandomWeapon / WeaponPickUp spawns (time, position)
    //   move:   per player each 0.5 s: hip speed (y, z), max since last line
    [BepInPlugin("com.sfmultiplayer.probe", "SFProbe", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static StreamWriter _w;
        private static float T { get { return Time.realtimeSinceStartup; } }
        private static readonly Dictionary<int, float> _bulletAt = new Dictionary<int, float>();
        private static readonly Dictionary<int, string> _bulletWhat = new Dictionary<int, string>();
        private static readonly Dictionary<int, float> _punchAt = new Dictionary<int, float>();
        private static float _blastAt = -10f;
        private static string _blastWhat = "";
        private readonly Dictionary<int, Vector3> _prevVel = new Dictionary<int, Vector3>();
        private bool _inFight;
        private float _nextScan, _nextMove;
        private readonly List<Rigidbody> _props = new List<Rigidbody>();
        private readonly List<Collider> _playerCols = new List<Collider>();
        private readonly Dictionary<int, float> _maxSpeed = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _maxUp = new Dictionary<int, float>();
        private static Type _ctrlT;

        internal static void W(string kind, string text)
        {
            try { if (_w != null) { _w.WriteLine(T.ToString("0.000") + "\t" + kind + "\t" + text); } } catch { }
        }

        private void Awake()
        {
            Log = Logger;
            if (Environment.GetEnvironmentVariable("SFPROBE") != "1") { Log.LogInfo("SFProbe: SFPROBE!=1, idle."); enabled = false; return; }
            _w = new StreamWriter(Path.Combine(Paths.GameRootPath, "SFProbe.log"), true);
            _w.AutoFlush = true;
            W("start", "SFProbe 1.0.0 fixedDeltaTime=" + Time.fixedDeltaTime + " gravity=" + Physics.gravity);
            _ctrlT = AccessTools.TypeByName("Controller");
            SceneManager.sceneLoaded += OnScene;
            var h = new Harmony("com.sfmultiplayer.probe");
            Post(h, "GameManager", "KillPlayer", "KillPost");
            Post(h, "GameManager", "AllButOnePlayersDied", "AllButOnePost");
            Post(h, "GameManager", "StartMatch", "StartMatchPost");
            Post(h, "GameManager", "StartCountDown", "CountDownPost");
            Post(h, "GameManager", "SpawnRandomWeapon", "SpawnWeaponPost");
            Post(h, "ProjectileCollision", "HitObject", "HitPost");
            Post(h, "PunchForce", "OnCollisionEnter", "PunchPost");
            Post(h, "Explosion", "Explode", "ExplodePost");
            Log.LogInfo("SFProbe active: logging to SFProbe.log");
        }

        private static void Post(Harmony h, string type, string method, string post)
        {
            try
            {
                var t = AccessTools.TypeByName(type);
                var m = t != null ? AccessTools.Method(t, method) : null;
                if (m == null) { W("warn", "no " + type + "." + method); return; }
                h.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), post)));
            }
            catch (Exception e) { W("warn", type + "." + method + ": " + e.Message); }
        }

        private void OnScene(Scene s, LoadSceneMode m) { W("scene", s.name + " index=" + s.buildIndex + " mode=" + m); _nextScan = 0f; }

        // ---- round flow
        internal static void KillPost(object playerToKill)
        {
            var c = playerToKill as Component;
            W("kill", c != null ? "player " + PlayerId(c) : "?");
        }
        internal static void AllButOnePost() { W("round", "AllButOnePlayersDied"); }
        internal static void StartMatchPost(object mapIndex, bool MovePlayers) { W("round", "StartMatch map=" + mapIndex + " move=" + MovePlayers); }
        internal static void CountDownPost() { W("round", "StartCountDown"); }
        internal static void SpawnWeaponPost() { W("weapon", "SpawnRandomWeapon"); }

        private static string PlayerId(Component c)
        {
            try { var f = AccessTools.Field(_ctrlT, "playerID"); var ctl = c.GetComponent(_ctrlT); return ctl != null ? f.GetValue(ctl).ToString() : "?"; } catch { return "?"; }
        }

        // ---- hits
        internal static void HitPost(object __instance, RaycastHit hit)
        {
            try
            {
                var rb = hit.rigidbody;
                if (rb == null) return;
                bool player = _ctrlT != null && rb.transform.root.GetComponent(_ctrlT) != null;
                var t = __instance.GetType();
                float force = (float)AccessTools.Field(t, "force").GetValue(__instance);
                float dmg = (float)AccessTools.Field(t, "damage").GetValue(__instance);
                string what = ((Component)__instance).name.Replace("(Clone)", "") + " force=" + force + " dmg=" + dmg;
                if (!player) { _bulletAt[rb.GetInstanceID()] = T; _bulletWhat[rb.GetInstanceID()] = what; }
                W("hit", (player ? "player " : "prop '" + rb.name + "' ") + "mass=" + rb.mass + " kin=" + rb.isKinematic + " v=" + rb.velocity.magnitude.ToString("0.00") + " by " + what);
            }
            catch { }
        }
        internal static void PunchPost(Collision collision)
        {
            try
            {
                var rb = collision.rigidbody;
                if (rb == null || (_ctrlT != null && rb.transform.root.GetComponent(_ctrlT) != null)) return;
                _punchAt[rb.GetInstanceID()] = T;
                W("punch", "prop '" + rb.name + "' mass=" + rb.mass + " relV=" + collision.relativeVelocity.magnitude.ToString("0.0"));
            }
            catch { }
        }
        internal static void ExplodePost(object __instance)
        {
            try
            {
                var c = (Component)__instance; var t = __instance.GetType();
                _blastAt = T;
                _blastWhat = c.name + " r=" + AccessTools.Field(t, "radius").GetValue(__instance) + " force=" + AccessTools.Field(t, "force").GetValue(__instance)
                    + " npForce=" + AccessTools.Field(t, "nonPlayerForce").GetValue(__instance) + " npVelChange=" + AccessTools.Field(t, "nonPlayerVelocityChange").GetValue(__instance);
                W("blast", _blastWhat + " at (" + c.transform.position.y.ToString("0.0") + "," + c.transform.position.z.ToString("0.0") + ")");
            }
            catch { }
        }

        // ---- sampling
        private void Update()
        {
            bool f = false;
            try { var fi = AccessTools.Field(AccessTools.TypeByName("GameManager"), "inFight"); f = (bool)fi.GetValue(null); } catch { }
            if (f != _inFight) { _inFight = f; W("round", "inFight=" + f); }
        }

        private void FixedUpdate()
        {
            if (T >= _nextScan) Rescan();
            foreach (var rb in _props)
            {
                if (rb == null || rb.isKinematic) continue;
                int id = rb.GetInstanceID();
                Vector3 v = rb.velocity, pv;
                if (_prevVel.TryGetValue(id, out pv))
                {
                    Vector3 dv = v - pv - Physics.gravity * Time.fixedDeltaTime;
                    if (dv.sqrMagnitude > 1f) W("jolt", "'" + rb.name + "' mass=" + rb.mass + " dv=" + dv.magnitude.ToString("0.00") + " v=" + v.magnitude.ToString("0.00") + " cause=" + Cause(rb, id));
                }
                _prevVel[id] = v;
            }
            if (T >= _nextMove) { _nextMove = T + 0.5f; SampleMove(); }
        }

        private string Cause(Rigidbody rb, int id)
        {
            float at;
            if (_bulletAt.TryGetValue(id, out at) && T - at < 0.1f) return "bullet " + _bulletWhat[id];
            if (_punchAt.TryGetValue(id, out at) && T - at < 0.1f) return "punch";
            if (T - _blastAt < 0.1f) return "blast " + _blastWhat;
            var b = new Bounds(); bool any = false;
            foreach (var c in rb.GetComponentsInChildren<Collider>()) { if (c.isTrigger) continue; if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds); }
            if (any) { b.Expand(0.15f); foreach (var pc in _playerCols) if (pc != null && pc.enabled && pc.bounds.Intersects(b)) return "player contact"; }
            return "other";
        }

        private void Rescan()
        {
            _nextScan = T + 2f;
            _props.Clear(); _playerCols.Clear();
            foreach (var rb in FindObjectsOfType<Rigidbody>())
            {
                if (_ctrlT != null && rb.transform.root.GetComponent(_ctrlT) != null) { foreach (var c in rb.GetComponents<Collider>()) if (!c.isTrigger) _playerCols.Add(c); continue; }
                if (rb.GetComponent("WeaponPickUp") != null) continue;
                _props.Add(rb);
            }
        }

        private void SampleMove()
        {
            if (_ctrlT == null) return;
            foreach (var o in FindObjectsOfType(_ctrlT))
            {
                var c = o as Component; if (c == null) continue;
                var hip = c.GetComponentInChildren(AccessTools.TypeByName("Hip")) as Component;
                var rb = hip != null ? hip.GetComponent<Rigidbody>() : null;
                if (rb == null) continue;
                int id = c.GetInstanceID();
                float hs = Mathf.Abs(rb.velocity.z), up = rb.velocity.y;
                W("move", "player " + PlayerId(c) + " vz=" + rb.velocity.z.ToString("0.00") + " vy=" + up.ToString("0.00") + " y=" + rb.position.y.ToString("0.00") + " z=" + rb.position.z.ToString("0.00"));
            }
        }
    }
}
