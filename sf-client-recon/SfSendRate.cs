using System;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // How often our player's state (PlayerUpdate: position, aim/shield, fight
    // state, shots) goes out. Stock asks for 50/s (mSendRatePerSecond) but
    // checks once per frame and resets its timer on each send, so the real
    // rate depends on the frame rate: 30/s at 60 fps, 48/s at 144 fps. Every
    // other player eases toward each update, so at 30/s we looked jerky on
    // their screens. With a 15 ms threshold the same per-frame check sends
    // ~60/s at 60 or 120 fps (48-72/s elsewhere): the same for every player,
    // no gameplay change, about 4 KB/s. SF_SEND_RATE=stock keeps stock.
    public partial class Plugin
    {
        private const float SendIntervalSec = 0.015f;
        private static readonly bool StockSendRate = Environment.GetEnvironmentVariable("SF_SEND_RATE") == "stock";
        private float _sendRateTickAt;
        private System.Reflection.FieldInfo _npSendRateF, _npLocalCtlSendF;
        private Type _npTypeSend;

        private void TickSendRate()
        {
            if (StockSendRate || Time.unscaledTime < _sendRateTickAt) return;
            _sendRateTickAt = Time.unscaledTime + 1f;
            if ((object)_npTypeSend == null)
            {
                _npTypeSend = CachedType("NetworkPlayer");
                if ((object)_npTypeSend == null) return;
                _npSendRateF = AccessTools.Field(_npTypeSend, "mSendRate");
                _npLocalCtlSendF = AccessTools.Field(_npTypeSend, "mHasLocalControl");
            }
            if ((object)_npSendRateF == null || (object)_npLocalCtlSendF == null) return;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_npTypeSend))
            {
                if (!(bool)_npLocalCtlSendF.GetValue(o)) continue;
                if ((float)_npSendRateF.GetValue(o) > SendIntervalSec) _npSendRateF.SetValue(o, SendIntervalSec);
            }
        }
    }
}
