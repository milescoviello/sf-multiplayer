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
        private void TickOracleDebugConsole()
        {
            if (!_batchModeHost) return;
            float now = Time.realtimeSinceStartup;
            if (_oracleDbgNextPollAt > 0f && now < _oracleDbgNextPollAt) return;
            _oracleDbgNextPollAt = now + 0.5f;
            try
            {
                if (!System.IO.File.Exists(OracleDbgCmdPath)) return;
                string text = System.IO.File.ReadAllText(OracleDbgCmdPath);
                if (string.IsNullOrEmpty(text) || text.Trim().Length == 0) return;
                System.IO.File.WriteAllText(OracleDbgCmdPath, "");   // consume
                string[] lines = text.Split('\n');
                foreach (var raw in lines)
                {
                    string cmd = raw.Trim();
                    if (cmd.Length == 0) continue;
                    if (cmd == "boxes") { OracleDumpBoxes(); continue; }
                    if (cmd == "rigs") { OracleDumpRigs(); continue; }
                    Log.LogInfo("[dbg] commands: boxes | rigs | help");
                }
            }
            catch (Exception e) { Log.LogWarning($"[dbg] poll error: {e.Message}"); }
        }

        private void OracleDumpBoxes()
        {
            EnsureNsoSrvCache();
            var sb = new System.Text.StringBuilder();
            sb.Append("[dbg] ORACLE BOXES id p=(y,z) v slp kin\n");
            int n = 0;
            foreach (var ent in _nsoSrvEntries)
            {
                if (!ent.Pushable) continue;
                var rb = ent.Rb;
                if ((object)rb == null || !ent.Comp) continue;
                Vector3 p;
                try { p = rb.position; } catch { continue; }
                sb.Append("  #").Append(ent.Id)
                  .Append(" p=(").Append(p.y.ToString("0.0")).Append(",").Append(p.z.ToString("0.0"))
                  .Append(") v=").Append(rb.velocity.magnitude.ToString("0.00"))
                  .Append(" slp=").Append(rb.IsSleeping() ? "1" : "0")
                  .Append(" kin=").Append(rb.isKinematic ? "1" : "0").Append("\n");
                n++;
            }
            sb.Append("[dbg] total ").Append(n).Append(" pushable crates (authority)");
            Log.LogInfo(sb.ToString());
        }

        private void OracleDumpRigs()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("[dbg] ORACLE RIGS:\n");
            int n = 0;
            foreach (var kv in SlotToRig)
            {
                var rig = kv.Value;
                if ((object)rig == null) continue;
                sb.Append("  slot=").Append(kv.Key)
                  .Append(" pos=").Append(rig.transform.position.ToString("0.00")).Append("\n");
                n++;
            }
            sb.Append("[dbg] total ").Append(n).Append(" rigs");
            Log.LogInfo(sb.ToString());
        }
        private void TickBoxDiagnostic()
        {
            if (Time.realtimeSinceStartup - _boxDiagLastAt < 5f) return;
            _boxDiagLastAt = Time.realtimeSinceStartup;
            try
            {
                if ((object)_nsoType == null)
                {
                    _nsoType = CachedType("NetworkSyncableObject");
                    if ((object)_nsoType == null) return;
                }
                var all = UnityEngine.Object.FindObjectsOfType(_nsoType);
                int total = all != null ? all.Length : 0;
                int voided = 0;
                float yMin = float.MaxValue, yMax = float.MinValue;
                Component sample = null;
                if (all != null)
                {
                    foreach (var nso in all)
                    {
                        var comp = nso as Component;
                        if ((object)comp == null) continue;
                        if ((object)sample == null && IsPushableCrateNso(comp.gameObject)) sample = comp;
                        float y = comp.transform.position.y;
                        if (y < yMin) yMin = y;
                        if (y > yMax) yMax = y;
                        if (y < -30f) voided++;
                    }
                }
                if (total == 0) yMin = yMax = 0f;

                // P0-23 floor probe. Unity 5.6 has ONE shared physics world (no
                // per-scene physics, transforms auto-sync), so a downward raycast
                // that hits nothing means the map's floor colliders are NOT
                // registered server-side — the root cause of boxes falling to the
                // void. Probe map-center and a sample crate's X/Z; also census all
                // colliders + a sample crate's physics state (layer/kinematic/enabled)
                // so we can also catch "floor exists but box can't collide with it".
                var colliders = UnityEngine.Object.FindObjectsOfType<Collider>();
                int colCount = colliders != null ? colliders.Length : 0;
                string floorCenter = ProbeFloorBelow(new Vector3(0f, 30f, 0f));
                string floorCrate = (object)sample != null
                    ? ProbeFloorBelow(new Vector3(sample.transform.position.x, 30f, sample.transform.position.z))
                    : "n/a";
                string crate = (object)sample != null ? DescribeNsoPhysics(sample) : "no-crate";
                Log.LogInfo($"[BOX-DIAG] nsos={total} void(y<-30)={voided} y=[{yMin:0.0},{yMax:0.0}] rigs={SlotToRig.Count} scene={SceneManager.GetActiveScene().name} colliders={colCount} gravityY={Physics.gravity.y:0.0} floor@center=[{floorCenter}] floor@crate=[{floorCrate}] crate={{{crate}}}");
            }
            catch (Exception e) { Log.LogWarning($"[BOX-DIAG] {e.Message}"); }
        }

        // Raycast straight down 200u from `from` and describe the first collider
        // hit — the definitive test for "is there a floor under the boxes on the
        // headless server?". "NONE" => no floor in the physics world (P0-23 root cause).
        private static string ProbeFloorBelow(Vector3 from)
        {
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 200f))
                return $"y={hit.point.y:0.0},obj='{hit.collider.gameObject.name}',layer={hit.collider.gameObject.layer},trig={hit.collider.isTrigger}";
            return "NONE(no floor!)";
        }

        // Sample crate physics state — if a floor IS found above but boxes still
        // fall, the box's own collider/layer/kinematic flag is the next suspect.
        private static string DescribeNsoPhysics(Component nso)
        {
            var rb = nso.GetComponentInChildren<Rigidbody>();
            var col = nso.GetComponentInChildren<Collider>();
            string rbs = (object)rb != null ? $"kinematic={rb.isKinematic}" : "no-rb";
            string cols = (object)col != null ? $"col.enabled={col.enabled},layer={col.gameObject.layer}" : "no-col";
            return $"{rbs},{cols}";
        }
    
        // Cheap per-call profiler for the Running loop (see heartbeat "hot:").
        private readonly System.Diagnostics.Stopwatch _profSw = new System.Diagnostics.Stopwatch();
        private readonly Dictionary<string, double> _profMs = new Dictionary<string, double>();
        private void ProfBegin() { _profSw.Reset(); _profSw.Start(); }
        private void ProfEnd(string name)
        {
            _profSw.Stop();
            double v; _profMs.TryGetValue(name, out v);
            _profMs[name] = v + _profSw.Elapsed.TotalMilliseconds;
        }
        internal string ProfTopAndReset(int frames)
        {
            if (frames <= 0 || _profMs.Count == 0) return "";
            var list = new List<KeyValuePair<string, double>>(_profMs);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < 7; i++)
                sb.Append($" {list[i].Key}={list[i].Value / frames:0.00}");
            _profMs.Clear();
            return sb.ToString();
        }

        // Box probe: how well shared objects (crates, cubes) come to rest after
        // a map loads. Records every NSO's local pose at scene load, then logs at
        // +4/+8/+12 s how many are still awake and how far they moved/tilted.
        // Boxes that "sit right" should be asleep with ~0 movement before anyone
        // touches them. Once per map; cheap.
        private readonly Dictionary<int, Vector3> _probePos = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, Quaternion> _probeRot = new Dictionary<int, Quaternion>();
        private float _probeStartAt = -1f; private int _probeStage; private int _probeScene;
        private readonly HashSet<int> _probeWasKinematic = new HashSet<int>();
        private readonly HashSet<int> _probeFallLogged = new HashSet<int>();
        private readonly Dictionary<int, Vector3> _probeWorld0 = new Dictionary<int, Vector3>();

        internal void StartBoxProbe(int sceneIndex)
        {
            _probePos.Clear(); _probeRot.Clear(); _probeWasKinematic.Clear(); _probeFallLogged.Clear(); _probeWorld0.Clear();
            _probeStartAt = Time.realtimeSinceStartup; _probeStage = 0; _probeScene = sceneIndex;
            try
            {
                var nsoType = CachedType("NetworkSyncableObject");
                if ((object)nsoType == null) return;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(nsoType))
                {
                    var c = o as Component; if (c == null) continue;
                    _probePos[c.GetInstanceID()] = c.transform.localPosition;
                    _probeRot[c.GetInstanceID()] = c.transform.localRotation;
                    _probeWorld0[c.GetInstanceID()] = c.transform.position;
                    var rbk = c.GetComponent<Rigidbody>();
                    if (rbk != null && rbk.isKinematic) _probeWasKinematic.Add(c.GetInstanceID());
                }
            }
            catch { }
        }

        private void TickBoxProbe()
        {
            if (_probeStartAt < 0f || _probeStage >= 10) return;
            float due = _probeStartAt + 4f * (_probeStage + 1);
            if (Time.realtimeSinceStartup < due) return;
            _probeStage++;
            try
            {
                var nsoType = CachedType("NetworkSyncableObject");
                int n = 0, awake = 0, moved = 0, released = 0, fallen = 0, layer24 = 0; float sumD = 0f, maxD = 0f, maxA = 0f;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(nsoType))
                {
                    var c = o as Component; if (c == null) continue;
                    Vector3 p0; Quaternion r0; int id = c.GetInstanceID();
                    if (!_probePos.TryGetValue(id, out p0) || !_probeRot.TryGetValue(id, out r0)) continue;
                    n++;
                    var rb = c.GetComponent<Rigidbody>();
                    if (rb != null && !rb.isKinematic && !rb.IsSleeping()) awake++;
                    if (rb != null && !rb.isKinematic && _probeWasKinematic.Contains(id)) released++;
                    if (c.gameObject.layer == 24) layer24++;
                    if (c.transform.position.y < -25f)
                    {
                        fallen++;
                        Vector3 w0;
                        if (_probeFallLogged.Count < 6 && _probeFallLogged.Add(id) && _probeWorld0.TryGetValue(id, out w0))
                            Log.LogInfo($"[box-probe] faller '{c.name}' started at {w0} (layer now {c.gameObject.layer}, rb {(rb == null ? "none" : (rb.isKinematic ? "kinematic" : "dynamic"))}, cd={(rb != null ? rb.collisionDetectionMode.ToString() : "-")})");
                    }
                    float d = Vector3.Distance(c.transform.localPosition, p0);
                    float a = Quaternion.Angle(c.transform.localRotation, r0);
                    sumD += d; if (d > maxD) maxD = d; if (a > maxA) maxA = a;
                    if (d > 0.1f || a > 5f) moved++;
                }
                // One sample crate's body settings + the global physics config.
                if (_probeStage == 1)
                {
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(nsoType))
                    {
                        var c = o as Component; if (c == null) continue;
                        var rb = c.GetComponent<Rigidbody>(); if (rb == null || rb.isKinematic) continue;
                        Log.LogInfo($"[box-probe] sample '{c.name}': v={rb.velocity.magnitude:0.000} w={rb.angularVelocity.magnitude:0.000} sleepThr={rb.sleepThreshold:0.000} mass={rb.mass:0.00} drag={rb.drag:0.00}/{rb.angularDrag:0.00} cd={rb.collisionDetectionMode} interp={rb.interpolation} constraints={rb.constraints} solverIt={rb.solverIterations} parent={(c.transform.parent != null ? c.transform.parent.name : "-")} | Physics: sleepThr={Physics.sleepThreshold:0.000} solverIt={Physics.defaultSolverIterations} contactOff={Physics.defaultContactOffset:0.000} gravity={Physics.gravity}");
                        break;
                    }
                }
                int ctrls = 0, ai = 0;
                try
                {
                    var ctrlType = CachedType("Controller");
                    var isAiF = (object)ctrlType != null ? HarmonyLib.AccessTools.Field(ctrlType, "isAI") : null;
                    foreach (var co in UnityEngine.Object.FindObjectsOfType(ctrlType))
                    {
                        ctrls++;
                        if ((object)isAiF != null && isAiF.GetValue(co) is bool isAi && isAi) ai++;
                    }
                }
                catch { }
                string mapInfo = $" controllers={ctrls} ai={ai} destructionsApplied={_destructApplied}";
                try
                {
                    var gmType = CachedType("GameManager");
                    var gm = HarmonyLib.AccessTools.Property(gmType, "Instance")?.GetValue(null, null);
                    var cmi = gm != null ? HarmonyLib.AccessTools.Field(gmType, "currentMapInfo")?.GetValue(gm) as Component : null;
                    var anim = gm != null ? HarmonyLib.AccessTools.Field(gmType, "mapAnimation")?.GetValue(gm) : null;
                    bool animating = anim != null && (HarmonyLib.AccessTools.Field(anim.GetType(), "isAnimating")?.GetValue(anim) is bool ba && ba);
                    if (cmi != null) mapInfo += $" mapRoot={cmi.transform.position} scale={cmi.transform.lossyScale.x:0.00} animating={animating}";
                }
                catch { }
                Log.LogInfo($"[box-probe] scene {_probeScene} +{4 * _probeStage}s: objects={n} awake={awake} moved(>0.1u|>5deg)={moved} frozenAtLoad->released={released} fellIntoVoid={fallen} onLayer24={layer24} meanMove={(n > 0 ? sumD / n : 0):0.000}u maxMove={maxD:0.00}u maxTilt={maxA:0}deg{mapInfo}");
            }
            catch (Exception e) { Log.LogWarning($"[box-probe] {e.Message}"); }
        }
}
}
