using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SFHeadlessHost
{
    // Per-lobby game rules. In stock Stick Fight the HOST decides these and
    // pushes them to every player (OptionsChanged: maps, HP, regen, weapon
    // spawn rate); the weapon pool and map pool come from the host's weapon-
    // and map-select menus. Here the server is the host, configured per lobby
    // by environment variables:
    //   SF_HP            100 | 200 | 300 | 1 | 25 | 50 | 75   (default 100)
    //   SF_REGEN         0 | 1                                 (default 0)
    //   SF_WEAPON_SPAWN  normal | high | low | none, or 0-3    (default normal)
    //   SF_WEAPONS       comma list of weapon names to allow ("Pistol,AK 47,
    //                    Snake:Gun"); "Category:Name" disambiguates. Unset = stock.
    //   SF_MAPS          comma list of map names to play ("Woods1,Castle18").
    //                    Unset = every stock Landfall map.
    public partial class Plugin
    {
        // ---------------- options pushed to clients ----------------
        private static readonly int[] HpByIndex = { 100, 200, 300, 1, 25, 50, 75 };   // OptionsHolder.NetworkOptionsChanged

        internal static byte HpOptionIndex
        {
            get
            {
                int hp;
                if (int.TryParse(Environment.GetEnvironmentVariable("SF_HP"), out hp))
                    for (int i = 0; i < HpByIndex.Length; i++) if (HpByIndex[i] == hp) return (byte)i;
                return 0;   // 100 HP
            }
        }

        internal static byte RegenOption => Environment.GetEnvironmentVariable("SF_REGEN") == "1" ? (byte)1 : (byte)0;

        // 0 normal (first drop 2-4 s, then every 5-8 s), 1 high (0.5-1 s, then 3-5 s),
        // 2 none, 3 low (4-8 s, then 8-12 s). Values from GameManager.StartMatch /
        // SpawnRandomWeapon.
        private static int _weaponSpawnOption = -1;
        internal static int WeaponSpawnOption
        {
            get
            {
                if (_weaponSpawnOption >= 0) return _weaponSpawnOption;
                string v = (Environment.GetEnvironmentVariable("SF_WEAPON_SPAWN") ?? "").Trim().ToLowerInvariant();
                int n;
                if (v == "high") _weaponSpawnOption = 1;
                else if (v == "none") _weaponSpawnOption = 2;
                else if (v == "low") _weaponSpawnOption = 3;
                else if (int.TryParse(v, out n) && n >= 0 && n <= 3) _weaponSpawnOption = n;
                else _weaponSpawnOption = 0;
                return _weaponSpawnOption;
            }
        }

        // OptionsChanged body: [maps][HP index][regen][weapon spawn rate].
        internal static byte[] LobbyOptionsBody() => new byte[] { 0, HpOptionIndex, RegenOption, (byte)WeaponSpawnOption };

        // Stock timer values for the current spawn option.
        internal static float FirstWeaponDelay()
        {
            switch (WeaponSpawnOption)
            {
                case 1: return UnityEngine.Random.Range(0.5f, 1f);
                case 3: return UnityEngine.Random.Range(4f, 8f);
                default: return UnityEngine.Random.Range(2f, 4f);
            }
        }

        internal static float NextWeaponDelay()
        {
            switch (WeaponSpawnOption)
            {
                case 1: return UnityEngine.Random.Range(3f, 5f);
                case 3: return UnityEngine.Random.Range(8f, 12f);
                default: return UnityEngine.Random.Range(5f, 8f);
            }
        }

        // ---------------- name filters ----------------
        private static string NormName(string s) => (s ?? "").Replace(" ", "").Trim().ToLowerInvariant();

        // Returns null when the variable is unset (= no filter). Entries are
        // "Name" or "Category:Name", normalised like the stock menus compare
        // names (spaces removed, case-insensitive).
        private static List<KeyValuePair<string, string>> ParseNameList(string envVar)
        {
            string raw = Environment.GetEnvironmentVariable(envVar);
            if (string.IsNullOrEmpty(raw)) return null;
            var list = new List<KeyValuePair<string, string>>();
            foreach (var tok in raw.Split(','))
            {
                var t = tok.Trim();
                if (t.Length == 0) continue;
                int c = t.IndexOf(':');
                if (c > 0) list.Add(new KeyValuePair<string, string>(NormName(t.Substring(0, c)), NormName(t.Substring(c + 1))));
                else list.Add(new KeyValuePair<string, string>(null, NormName(t)));
            }
            return list;
        }

        private static bool NameListContains(List<KeyValuePair<string, string>> list, string category, string name)
        {
            string c = NormName(category), n = NormName(name);
            foreach (var kv in list)
                if (kv.Value == n && (kv.Key == null || kv.Key == c)) return true;
            return false;
        }

        // ---------------- map rotation from the stock map-select list ----------------
        // MapSelectionHandler.m_Categories[*].CategoryMaps[*] (SingleMapUI: MapName,
        // CategoryName, MapIndex = the Landfall scene index, IsLocallyActive,
        // MapTypeEnum). The old hardcoded rotation ("scenes 6-124 minus 102") also
        // contained scenes that are not maps in the stock list (e.g. 10).
        private static bool _mapRotationBuilt;

        internal static void BuildMapRotationFromGame()
        {
            if (_mapRotationBuilt) return;
            _mapRotationBuilt = true;
            try
            {
                var mshType = AccessTools.TypeByName("MapSelectionHandler");
                if ((object)mshType == null) { Log.LogWarning("[maps] MapSelectionHandler type not found; keeping the default rotation."); return; }
                UnityEngine.Object handler = UnityEngine.Object.FindObjectOfType(mshType);
                if (handler == null)
                {
                    var all = Resources.FindObjectsOfTypeAll(mshType);
                    if (all != null && all.Length > 0) handler = all[0];
                }
                var cats = handler != null ? AccessTools.Field(mshType, "m_Categories")?.GetValue(handler) as System.Collections.IList : null;
                if (cats == null || cats.Count == 0) { Log.LogWarning("[maps] map-select list empty; keeping the default rotation."); return; }
                var wanted = ParseNameList("SF_MAPS");
                var rotation = new List<int>();
                var table = new System.Text.StringBuilder();
                foreach (var cat in cats)
                {
                    var maps = AccessTools.Field(cat.GetType(), "CategoryMaps")?.GetValue(cat) as System.Collections.IList;
                    if (maps == null) continue;
                    foreach (var m in maps)
                    {
                        if (m == null) continue;
                        var t = m.GetType();
                        string name = AccessTools.Field(t, "MapName")?.GetValue(m) as string;
                        string category = AccessTools.Field(t, "CategoryName")?.GetValue(m) as string;
                        string index = AccessTools.Field(t, "MapIndex")?.GetValue(m) as string;
                        object type = AccessTools.Field(t, "MapTypeEnum")?.GetValue(m);
                        bool active = AccessTools.Field(t, "IsLocallyActive")?.GetValue(m) is bool b && b;
                        int scene;
                        if (type == null || type.ToString() != "Landfall" || !int.TryParse(index, out scene)) continue;
                        table.Append($" {name}={scene}{(active ? "" : "(off)")}");
                        bool use = wanted != null ? NameListContains(wanted, category, name) : active;
                        if (use && scene != 102) rotation.Add(scene);
                    }
                }
                Log.LogInfo($"[maps] stock Landfall maps:{table}");
                if (rotation.Count == 0) { Log.LogWarning("[maps] no maps matched; keeping the default rotation."); return; }
                _allLandfallMaps = rotation.ToArray();
                Log.LogInfo($"[maps] rotation: {rotation.Count} maps ({(wanted != null ? "SF_MAPS" : "stock defaults")}).");
            }
            catch (Exception e) { Log.LogWarning($"[maps] rotation build failed: {e.Message}"); }
        }
    }
}
