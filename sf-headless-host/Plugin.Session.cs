using System;
using System.Net;
using UnityEngine;

namespace SFHeadlessHost
{
    // Session tokens. Every player reaches the server through the router, so
    // the server sees them all at 127.0.0.1 and an address says nothing about
    // who sent a packet. At ClientInit each player gets a random token over
    // their game socket (msgType 50); the client plugin (0.7.2+) repeats it:
    //  - msgType 51 on the GAME socket every 2 s ([u64 steamId][u32 token]):
    //    if it arrives from a new address (our router restarted, or the
    //    player's home router changed their port), the player is moved there
    //    instead of becoming a stranger whose movement is dropped and who is
    //    "left" 30 s later;
    //  - appended to msgType 48 on the v26 socket: binds that player's
    //    snapshot address. Once bound, PlayerInput / fire / viewer packets
    //    from any other address can no longer claim that slot or redirect its
    //    snapshots (before, any client could, all being 127.0.0.1).
    // Players on older clients keep the old behaviour.
    public partial class Plugin
    {
        private const byte PktSessionToken = 50;   // server -> client, game socket: [u32 token]
        private const byte PktGameHello = 51;      // client -> server, game socket: [u64 steamId][u32 token]
        private static readonly System.Random _tokenRng = new System.Random();
        private float _tokenTickAt;

        private void IssueSessionToken(SfClient cli)
        {
            uint t;
            do { t = (uint)_tokenRng.Next(1, int.MaxValue) ^ ((uint)_tokenRng.Next() << 1); } while (t == 0);
            cli.Token = t;
            cli.TokenConfirmed = false;
            cli.V26Bound = false;
            SendSessionToken(cli);
        }

        private void SendSessionToken(SfClient cli)
        {
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(cli.Token >> (8 * i));
            _recSuppress = true;   // never in a replay
            try { SendSfPacket(cli.Addr, PktSessionToken, b, cli.SteamID, 0); } finally { _recSuppress = false; }
            cli.TokenSentAt = Time.realtimeSinceStartup;
        }

        // Main loop: resend until the client confirms (a lost packet, or an
        // older client that never will: give up after a minute).
        private void TickSessionTokens()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _tokenTickAt) return;
            _tokenTickAt = now + 1f;
            foreach (var kv in _sfClients)
            {
                var c = kv.Value;
                if (!c.Initialized || c.Token == 0 || c.TokenConfirmed) continue;
                if (now - c.TokenSentAt > 3f && now - c.InitializedAt < 60f) SendSessionToken(c);
            }
        }

        private SfClient ClientByToken(ulong sid, uint token)
        {
            if (token == 0) return null;
            foreach (var kv in _sfClients)
                if (kv.Value.Initialized && kv.Value.SteamID == sid && kv.Value.Token == token) return kv.Value;
            return null;
        }

        // msgType 51 on the game socket.
        private void HandleGameHello(byte[] data, int off, int len, IPEndPoint from)
        {
            if (len < 12) return;
            ulong sid = BitConverter.ToUInt64(data, off);
            uint token = BitConverter.ToUInt32(data, off + 8);
            var cli = ClientByToken(sid, token);
            if (cli == null) { SendSessionUnknown(from, sid); return; }
            cli.TokenConfirmed = true;
            cli.LastSeen = Time.realtimeSinceStartup;
            string key = from.ToString();
            string oldKey = cli.Addr.ToString();
            if (oldKey == key) return;
            _sfClients.Remove(oldKey);
            _sfClients.Remove(key);    // a stranger entry made by its first packets from the new address
            Log.LogInfo($"[SF] slot {cli.Slot} moved from {cli.Addr} to {from} (same session token).");
            cli.Addr = from;
            _sfClients[key] = cli;
        }

        // A token this lobby never issued: the lobby restarted under the player
        // (a deploy, the watchdog, RST) or dropped them (timed out). Before, such
        // a player sat in a dead match until they quit, and their client kept
        // claiming its old slot. msgType 52 tells the client to rejoin; at most
        // once a second per address.
        private const byte PktSessionUnknown = 52;  // server -> client, game socket: empty body
        private readonly System.Collections.Generic.Dictionary<string, float> _unknownSentAt = new System.Collections.Generic.Dictionary<string, float>();
        private void SendSessionUnknown(IPEndPoint from, ulong sid)
        {
            float now = Time.realtimeSinceStartup;
            string key = from.ToString();
            float at;
            if (_unknownSentAt.TryGetValue(key, out at) && now - at < 1f) return;
            if (_unknownSentAt.Count > 256) _unknownSentAt.Clear();
            _unknownSentAt[key] = now;
            _recSuppress = true;
            try { SendSfPacket(from, PktSessionUnknown, new byte[0], sid, 0); } finally { _recSuppress = false; }
            if (!_unknownLogged.Contains(key))
            {
                _unknownLogged.Add(key);
                Log.LogInfo($"[SF] {from} has a session this lobby doesn't know (lobby restarted or player dropped): asked it to rejoin.");
            }
        }
        private readonly System.Collections.Generic.HashSet<string> _unknownLogged = new System.Collections.Generic.HashSet<string>();

        // Token tail of msgType 48: bind the snapshot (v26) address.
        private void BindV26ByToken(ulong sid, uint token, IPEndPoint from)
        {
            var cli = ClientByToken(sid, token);
            if (cli == null || cli.Slot < 0) return;
            cli.TokenConfirmed = true;
            IPEndPoint prev;
            bool changed = !_slotV26Endpoint.TryGetValue(cli.Slot, out prev) || !prev.Equals(from);
            cli.V26Bound = true;
            if (!changed) return;
            _slotV26Endpoint[cli.Slot] = from;
            Log.LogInfo($"[SF] slot {cli.Slot} v26 endpoint {from} (session token).");
        }

        // An address may act for this slot on the v26 socket.
        private bool V26AddressOk(SfClient owner, IPEndPoint from)
        {
            if (owner == null || !owner.V26Bound) return true;   // old client: address check only
            IPEndPoint bound;
            return _slotV26Endpoint.TryGetValue(owner.Slot, out bound) && bound.Equals(from);
        }
    }
}
