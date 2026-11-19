using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

internal static class AimZoom
{
    internal static ConfigEntry<float> Amount;

    static FieldInfo _cam, _cm, _eye, _lastFov;
    static MethodInfo _setFov;
    static float _k; static int _frame; static bool _zoomed, _said;

    internal static void Init(ConfigFile cfg, HarmonyLib.Harmony h)
    {
        Amount = cfg.Bind("Keys", "AimZoom", 45f, new ConfigDescription("Third person: how much closer the camera comes to your shoulder while you aim, percent. 0 = off.", new AcceptableValueRange<float>(0f, 75f)));
        var t = AccessTools.TypeByName("ThirdPerson.ThirdPersonMod");
        var drive = t == null ? null : AccessTools.Method(t, "Drive");
        if (drive == null) { Plugin.Out.LogWarning("[Zoom] third person camera not found: no aim zoom"); return; }
        _cam = AccessTools.Field(t, "_cam"); _cm = AccessTools.Field(t, "_cm"); _eye = AccessTools.Field(t, "_eye"); _lastFov = AccessTools.Field(t, "_lastFovWritten");
        var g = AccessTools.TypeByName("ThirdPerson.Game");
        _setFov = g == null ? null : AccessTools.Method(g, "SetCameraFov");
        if (_cam == null || _eye == null) { Plugin.Out.LogWarning("[Zoom] third person camera parts not found: no aim zoom"); return; }
        h.Patch(drive, postfix: new HarmonyMethod(typeof(AimZoom), nameof(After)));
        Plugin.Out.LogInfo("[Zoom] ready, " + Amount.Value.ToString("0") + "% closer while aiming in third person" + (_setFov != null && _lastFov != null ? "" : " (no lens change)"));
    }

    public static void After(object __instance)
    {
        try
        {
            if (Amount == null) return;
            float amount = Amount.Value / 100f;
            bool want = amount > 0.01f && !Drone.Flying && Plugin.ThirdPerson && Plugin.Ads;
            if (Time.frameCount != _frame)
            {
                _frame = Time.frameCount;
                _k = Mathf.MoveTowards(_k, want ? 1f : 0f, Time.unscaledDeltaTime * (want ? 6f : 7f));
            }
            if (_k <= 0.0001f)
            {
                if (_zoomed) { _zoomed = false; if (_lastFov != null) _lastFov.SetValue(__instance, -1f); }
                return;
            }
            var cam = _cam.GetValue(__instance) as Camera;
            var eye = _eye.GetValue(__instance) as Transform;
            if (cam == null || eye == null) return;
            float e = _k * _k * (3f - 2f * _k);
            var tr = cam.transform;
            Vector3 pos = tr.position, head = eye.position;
            tr.position = Vector3.Lerp(pos, head + (pos - head) * (1f - amount), e);
            if (_setFov != null && _lastFov != null)
            {
                float baseFov = (float)_lastFov.GetValue(__instance);
                if (baseFov > 1f) _setFov.Invoke(null, new object[] { cam, _cm != null ? _cm.GetValue(__instance) : null, baseFov * (1f - 0.35f * amount * e) });
            }
            _zoomed = true;
            if (!_said) { _said = true; Plugin.Out.LogInfo("[Zoom] aiming in third person: camera moved " + (amount * 100f).ToString("0") + "% closer"); }
        }
        catch (Exception ex) { Plugin.Out.LogDebug("[Zoom] " + ex.Message); }
    }
}
