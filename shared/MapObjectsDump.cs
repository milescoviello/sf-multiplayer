using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace SFShared
{
    // Test aid shared by the server (bridge "mapobjs") and the client (debug
    // console "mapobjs"): one line per map gimmick (MapInfoSyncableBase) and per
    // weapon on the ground, so the server's and each client's copies can be
    // compared at the same moment. Sorted, so two dumps diff line by line.
    //   G/M/P/...  first letter of the gimmick type, (start pos key), position,
    //              GetData() hex (GhostPlatform: on; MoveAlongPathUsingForce: target),
    //              GhostPlatform also isOn and whether its colliders are on
    //   W #index   weapon spawn index, position, sleeping
    internal static class MapObjectsDump
    {
        private static Type _baseT, _gpT, _wT;
        private static FieldInfo _startF, _gpIsOnF, _gpColsF, _wIdxF;
        private static MethodInfo _getDataM;

        private static Type FindType(string name)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (a.GetName().Name != "Assembly-CSharp") continue;
                var t = a.GetType(name, false);
                if ((object)t != null) return t;
            }
            return null;
        }

        internal static string Text()
        {
            if ((object)_baseT == null)
            {
                _baseT = FindType("MapInfoSyncableBase");
                _gpT = FindType("GhostPlatform");
                _wT = FindType("WeaponPickUp");
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                if ((object)_baseT != null) { _startF = _baseT.GetField("m_StartPos", F); _getDataM = _baseT.GetMethod("GetData", F); }
                if ((object)_gpT != null) { _gpIsOnF = _gpT.GetField("isOn", F); _gpColsF = _gpT.GetField("colliders", F); }
                if ((object)_wT != null) _wIdxF = _wT.GetField("mNetworkSpawnIndex", F);
            }
            var rows = new List<string>();
            if ((object)_baseT != null)
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(_baseT))
                {
                    var c = o as Component;
                    if (c == null) continue;
                    Vector2 sp = (object)_startF != null ? (Vector2)_startF.GetValue(o) : Vector2.zero;
                    string hex = "";
                    try
                    {
                        var d = (object)_getDataM != null ? _getDataM.Invoke(o, null) as byte[] : null;
                        if (d != null) foreach (var b in d) hex += b.ToString("X2");
                    }
                    catch { hex = "?"; }
                    var p = c.transform.position;
                    string row = c.GetType().Name.Substring(0, 1) + " " + c.GetType().Name + " (" + sp.x.ToString("0.0") + "," + sp.y.ToString("0.0")
                        + ") p=(" + p.y.ToString("0.00") + "," + p.z.ToString("0.00") + ") d=" + hex;
                    if ((object)_gpT != null && _gpT.IsInstanceOfType(o))
                    {
                        bool on = (object)_gpIsOnF != null && (bool)_gpIsOnF.GetValue(o);
                        bool col = false;
                        var cols = (object)_gpColsF != null ? _gpColsF.GetValue(o) as Collider[] : null;
                        if (cols != null) foreach (var cc in cols) if (cc != null && cc.enabled) { col = true; break; }
                        row += " on=" + (on ? 1 : 0) + " col=" + (col ? 1 : 0);
                    }
                    rows.Add(row);
                }
            }
            if ((object)_wT != null)
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(_wT))
                {
                    var c = o as Component;
                    if (c == null) continue;
                    ushort idx = 0;
                    try { if ((object)_wIdxF != null) idx = (ushort)_wIdxF.GetValue(o); } catch { }
                    var p = c.transform.position;
                    var rb = c.GetComponent<Rigidbody>();
                    rows.Add("W #" + idx + " p=(" + p.y.ToString("0.00") + "," + p.z.ToString("0.00") + ") slp=" + (rb != null && rb.IsSleeping() ? 1 : 0));
                }
            }
            rows.Sort(StringComparer.Ordinal);
            return string.Join("\n", rows.ToArray());
        }
    }
}
