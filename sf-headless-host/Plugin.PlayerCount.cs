using System;
using System.IO;
using UnityEngine;

namespace SFHeadlessHost
{
    // The lobby list showed players who had left: the router estimated players
    // from its open flows (two per player), which stay open 45 s after a player
    // goes quiet, so "2/4" lingered on an empty lobby (and RST stayed greyed
    // out). The lobby knows exactly who is in it: every 2 s it writes the count
    // to <registry>/<CODE>.players, which the router uses while it is fresh.
    public partial class Plugin
    {
        private float _playerCountAt;
        private int _playerCountWritten = -1;
        private float _playerCountWrittenAt;

        private void TickPlayerCountFile()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _playerCountAt) return;
            _playerCountAt = now + 2f;
            string code = Environment.GetEnvironmentVariable("SF_LOBBY_CODE");
            if (string.IsNullOrEmpty(code) || code.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
            int n = 0;
            foreach (var kv in _sfClients) if (IsParticipant(kv.Value)) n++;
            // Unchanged: refresh the file every 6 s anyway (the router treats
            // an old file as a lobby that stopped reporting).
            if (n == _playerCountWritten && now - _playerCountWrittenAt < 6f) return;
            string dir = Environment.GetEnvironmentVariable("SF_LOBBY_REGISTRY") ?? "/tmp/sf-lobbies";
            try
            {
                // Under Proton a Unix path is reachable through drive Z:.
                string winDir = dir.StartsWith("/") ? "Z:" + dir.Replace('/', '\\') : dir;
                if (!Directory.Exists(winDir)) return;
                string path = Path.Combine(winDir, code.ToUpperInvariant() + ".players");
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, n + "\n");
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                _playerCountWritten = n;
                _playerCountWrittenAt = now;
            }
            catch (Exception e)
            {
                if (_playerCountWritten != -2) Log.LogWarning($"[lobby] player count file: {e.Message}");
                _playerCountWritten = -2;
            }
        }
    }
}
