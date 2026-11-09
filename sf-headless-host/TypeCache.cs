using System;
using System.Collections.Generic;
using HarmonyLib;

namespace SFHeadlessHost
{
    public partial class Plugin
    {
        // AccessTools.TypeByName walks every type of every loaded assembly when
        // the name isn't a fully qualified one, and this plugin calls it on hot
        // per-frame paths. Game types never change after load, so cache hits
        // (misses are retried: the type may simply not be loaded yet).
        private static readonly Dictionary<string, Type> _typeCache = new Dictionary<string, Type>();
        internal static Type CachedType(string name)
        {
            Type t;
            if (_typeCache.TryGetValue(name, out t)) return t;
            t = AccessTools.TypeByName(name);
            if ((object)t != null) _typeCache[name] = t;
            return t;
        }
    }
}
