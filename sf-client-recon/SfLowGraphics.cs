using System;
using UnityEngine;

namespace SFClientRecon
{
    // Low-end PCs: Stick Fight's post-processing (Unity's post-processing stack:
    // bloom, colour grading, antialiasing...) is several full-screen passes, and
    // on an old GPU that is most of the frame. A 2009 MacBook Pro (GeForce
    // 9400M) runs the menu at 10 fps at 1280x800 and 19 fps at 640x400: fill
    // rate, not the CPU.
    //
    // So when the game holds under 40 fps for a 20 s stretch (loading hitches
    // left out), post-processing is turned off for the rest of this session. It
    // only changes how the game looks on this PC, nothing is saved, and the next
    // start measures again. A frame cap the player set in the game's settings
    // doesn't count. SF_LOW_GFX=1 forces it on, SF_LOW_GFX=0 or a file
    // BepInEx/sf-full-graphics.on keeps the full look.
    public partial class Plugin
    {
        private const float LowGfxBelowFps = 40f;
        private const float LowGfxWindowSec = 20f;
        private int _lowGfx;              // 0 = measuring, 1 = on, -1 = never
        private float _lowGfxFrom = -1f, _lowGfxApplyAt;
        private int _lowGfxFrames;
        private Type _ppType;

        private void TickLowGraphics()
        {
            if (_lowGfx < 0) return;
            float now = Time.realtimeSinceStartup;
            if (_lowGfx == 0)
            {
                if (_lowGfxFrom < 0f)
                {
                    string env = Environment.GetEnvironmentVariable("SF_LOW_GFX");
                    bool keep = env == "0";
                    try { keep |= System.IO.File.Exists(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "sf-full-graphics.on")); } catch { }
                    if (keep) { _lowGfx = -1; Log.LogInfo("[gfx] full graphics kept (SF_LOW_GFX=0 or sf-full-graphics.on)."); return; }
                    if (env == "1") { LowGraphicsOn("SF_LOW_GFX=1"); return; }
                    _lowGfxFrom = now + 30f;   // the first 30 s are loading
                    return;
                }
                if (now < _lowGfxFrom) return;
                // A hitch (loading a map, a stall) starts the stretch over.
                if (Time.unscaledDeltaTime > 0.5f) { _lowGfxFrom = now; _lowGfxFrames = 0; return; }
                _lowGfxFrames++;
                float span = now - _lowGfxFrom;
                if (span < LowGfxWindowSec) return;
                float fps = _lowGfxFrames / span;
                _lowGfxFrom = now; _lowGfxFrames = 0;
                // A frame cap the player chose (the game's FPS setting) isn't a slow PC.
                int cap = Application.targetFrameRate;
                if (cap > 0 && cap < LowGfxBelowFps + 5f) return;
                if (fps < LowGfxBelowFps) LowGraphicsOn(fps.ToString("0") + " fps for " + LowGfxWindowSec.ToString("0") + " s");
                return;
            }
            // On: new cameras come with scenes, so look again every second.
            if (now < _lowGfxApplyAt) return;
            _lowGfxApplyAt = now + 1f;
            if ((object)_ppType == null) return;
            var cams = Camera.allCameras;
            for (int i = 0; i < cams.Length; i++)
            {
                var pp = cams[i].GetComponent(_ppType) as Behaviour;
                if (pp != null && pp.enabled) pp.enabled = false;
            }
        }

        private void LowGraphicsOn(string why)
        {
            _ppType = CachedType("UnityEngine.PostProcessing.PostProcessingBehaviour");
            if ((object)_ppType == null) { _lowGfx = -1; Log.LogWarning("[gfx] post-processing type not found: low graphics unavailable."); return; }
            _lowGfx = 1;
            _lowGfxApplyAt = 0f;
            Log.LogInfo("[gfx] " + why + ": post-processing off for this session (it's the slowest part on older GPUs). "
                + "To keep the full look: create BepInEx/sf-full-graphics.on.");
        }
    }
}
