using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFServerBrowser
{
    // ============================================================================
    //  Scoreboard — the stock win counter, always on in a match.
    //
    //  Stock Stick Fight shows round wins in WinCounterUI (coloured numbers, one
    //  per player) when the player's own "Show Wins" setting is on; DSF comp plays
    //  with it on, and our lobbies show it for everyone. F4 hides/shows it.
    //
    //  This used to draw its own "RED vs BLUE" team bar and switch the stock
    //  WinCounterUI GameObject OFF during matches. That was wrong twice: slot 0
    //  is yellow, not red, and an inactive WinCounterUI made the game's own
    //  StartMatch → FindObjectOfType<WinCounterUI>().IncrementWinCounter throw,
    //  which broke the map transition whenever a round had a winner.
    // ============================================================================
    internal class Scoreboard : MonoBehaviour
    {
        internal Plugin Owner;

        private bool _hidden;            // F4 toggle
        private float _nextPoll;

        private static bool _reflTried;
        private static Type _winUiType;
        private static MethodInfo _toggle;

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F4)) { _hidden = !_hidden; _nextPoll = 0f; }
            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.5f;
            if (Owner == null || !Owner.UiInMatch) return;
            try { ShowStockWinCounter(!_hidden); }
            catch (Exception e) { Plugin.Log.LogWarning("[scoreboard] " + e.Message); }
        }

        // ToggleWinCounterVisibility(true) also refreshes the numbers, so calling
        // it every poll picks up players who joined after the last round.
        private static void ShowStockWinCounter(bool show)
        {
            if (!_reflTried)
            {
                _reflTried = true;
                _winUiType = AccessTools.TypeByName("WinCounterUI");
                if ((object)_winUiType != null) _toggle = AccessTools.Method(_winUiType, "ToggleWinCounterVisibility");
            }
            if ((object)_winUiType == null || (object)_toggle == null) return;
            var ui = UnityEngine.Object.FindObjectOfType(_winUiType) as Component;
            if ((object)ui == null)
            {
                // Re-enable one that was switched off (older builds of this plugin did that).
                foreach (var o in Resources.FindObjectsOfTypeAll(_winUiType))
                {
                    var c = o as Component;
                    if ((object)c != null && c.gameObject.scene.IsValid()) { c.gameObject.SetActive(true); ui = c; break; }
                }
            }
            if ((object)ui != null) _toggle.Invoke(ui, new object[] { show });
        }
    }
}
