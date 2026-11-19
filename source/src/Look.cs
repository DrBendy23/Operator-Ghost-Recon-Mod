using System;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace CinematicLook;

internal static class Look
{
    static GameObject _go; static Volume _vol; static VolumeProfile _prof;
    static Tonemapping _tone; static ColorAdjustments _col; static WhiteBalance _wb; static Vignette _vig; static FilmGrain _grain; static Bloom _bloom;
    static ShadowsMidtonesHighlights _smh; static ChromaticAberration _ca;
    static int _lastPreset = -1;

    struct P { public float exposure, contrast, saturation, warmth, vignette, grain, bloom, shadows, fringe; }
    static readonly P[] Presets =
    {
        default,
        new P { exposure = 0f, contrast = 8f, saturation = -6f, warmth = 0f, vignette = 8f, grain = 6f, bloom = 6f, shadows = 0.03f, fringe = 0.08f },
        new P { exposure = 0f, contrast = 15f, saturation = -8f, warmth = -6f, vignette = 30f, grain = 20f, bloom = 15f },
        new P { exposure = 0.1f, contrast = 10f, saturation = 6f, warmth = 14f, vignette = 25f, grain = 10f, bloom = 20f },
        new P { exposure = -0.1f, contrast = 14f, saturation = -22f, warmth = -18f, vignette = 30f, grain = 18f, bloom = 10f },
        new P { exposure = 0f, contrast = 28f, saturation = -100f, warmth = 0f, vignette = 42f, grain = 40f, bloom = 12f },
    };

    internal static void Apply()
    {
        if (!Plugin.On.Value || Plugin.Strength.Value <= 0f) { if (_vol != null) _vol.weight = 0f; return; }
        if (_vol == null && !Build()) return;
        int preset = Plugin.Preset.Value;
        if (preset != _lastPreset)
        {
            _lastPreset = preset;
            if (preset > 0)
            {
                var p = Presets[preset];
                Plugin.Exposure.Value = p.exposure; Plugin.Contrast.Value = p.contrast; Plugin.Saturation.Value = p.saturation; Plugin.Warmth.Value = p.warmth;
                Plugin.Vignette.Value = p.vignette; Plugin.Grain.Value = p.grain; Plugin.Bloom.Value = p.bloom;
            }
            var q = preset > 0 ? Presets[preset] : default;
            _smh.shadows.overrideState = true; _smh.shadows.value = new Vector4(1f, 1f, 1f, q.shadows);
            Set(_ca.intensity, q.fringe);
        }
        _vol.weight = Mathf.Clamp01(Plugin.Strength.Value / 100f);
        Set(_col.postExposure, Plugin.Exposure.Value);
        Set(_col.contrast, Plugin.Contrast.Value);
        Set(_col.saturation, Plugin.Saturation.Value);
        Set(_wb.temperature, Plugin.Warmth.Value);
        Set(_vig.intensity, Plugin.Vignette.Value / 100f);
        Set(_grain.intensity, Plugin.Grain.Value / 100f);
        Set(_bloom.intensity, Plugin.Bloom.Value / 100f);
    }

    static void Set(FloatParameter p, float v) { p.overrideState = true; p.value = v; }

    static bool Build()
    {
        try
        {
            _prof = ScriptableObject.CreateInstance<VolumeProfile>();
            _prof.hideFlags = HideFlags.HideAndDontSave;
            _tone = _prof.Add<Tonemapping>(true); _tone.mode.overrideState = true; _tone.mode.value = TonemappingMode.ACES;
            _col = _prof.Add<ColorAdjustments>(true);
            _wb = _prof.Add<WhiteBalance>(true);
            _vig = _prof.Add<Vignette>(true); _vig.smoothness.overrideState = true; _vig.smoothness.value = 0.45f; _vig.roundness.overrideState = true; _vig.roundness.value = 0.8f;
            _grain = _prof.Add<FilmGrain>(true); _grain.type.overrideState = true; _grain.type.value = FilmGrainLookup.Medium1; _grain.response.overrideState = true; _grain.response.value = 0.8f;
            _smh = _prof.Add<ShadowsMidtonesHighlights>(true);
            _ca = _prof.Add<ChromaticAberration>(true);
            _bloom = _prof.Add<Bloom>(true); _bloom.threshold.overrideState = true; _bloom.threshold.value = 0.9f; _bloom.scatter.overrideState = true; _bloom.scatter.value = 0.7f;
            _go = new GameObject("CinematicLookVolume") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _vol = _go.AddComponent<Volume>();
            _vol.isGlobal = true; _vol.priority = 1000f; _vol.sharedProfile = _prof; _vol.weight = 0f;
            Plugin.Out.LogInfo("Cinematic volume ready.");
            return true;
        }
        catch (Exception e) { Plugin.Out.LogWarning("Cinematic volume: " + e.Message); _vol = null; return false; }
    }
}

