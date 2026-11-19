using System;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

internal static class Doorway
{
    internal static ConfigEntry<bool> On;
    internal static ConfigEntry<float> Hold, Speed;

    const float EnterAt = 0.7f, LeaveAt = 0.9f, BackSeconds = 0.25f;
    static FieldInfo _sq, _target, _in, _out, _secs;
    static float _lastIn = -99f, _before, _origOut = -1f, _origSecs, _origIn;
    static bool _leaving;

    internal static void Init(ConfigFile cfg, HarmonyLib.Harmony h)
    {
        On = cfg.Bind("Keys", "DoorwayFirstPerson", true, "Third person: doorways and tight spots switch you to first person faster and keep you there a moment once you are through. Off: the Archduke default.");
        Hold = cfg.Bind("Keys", "DoorwayHoldSecondsV2", 1f, new ConfigDescription("Third person: after a doorway or tight spot puts you in first person, stay in first person this long once you are through, seconds. 0 = no hold.", new AcceptableValueRange<float>(0f, 4f)));
        Speed = cfg.Bind("Keys", "DoorwaySwitchSecondsV2", 0.05f, new ConfigDescription("Third person: how fast the view goes to first person at a doorway, seconds. The Archduke default is 0.15.", new AcceptableValueRange<float>(0.03f, 0.3f)));
        var t = AccessTools.TypeByName("ThirdPerson.Rig");
        var solve = t == null ? null : AccessTools.Method(t, "Solve");
        _sq = t == null ? null : AccessTools.Field(t, "Squeeze");
        _target = t == null ? null : AccessTools.Field(t, "_squeezeTarget");
        _in = t == null ? null : AccessTools.Field(t, "SqueezeIn");
        _out = t == null ? null : AccessTools.Field(t, "SqueezeOut");
        _secs = t == null ? null : AccessTools.Field(t, "SqueezeSeconds");
        if (solve == null || _sq == null || _target == null || _in == null || _out == null || _secs == null) { Plugin.Out.LogWarning("[Door] third person doorway view not found: Archduke default"); return; }
        h.Patch(solve, prefix: new HarmonyMethod(typeof(Doorway), nameof(Before)), postfix: new HarmonyMethod(typeof(Doorway), nameof(After)));
    }

    public static void Before(object __instance, bool first)
    {
        if (!first) return;
        try
        {
            float sq = (float)_sq.GetValue(__instance);
            _before = sq;
            if (_origOut < 0f) { _origOut = (float)_out.GetValue(__instance); _origSecs = (float)_secs.GetValue(__instance); _origIn = (float)_in.GetValue(__instance); }
            if (!On.Value)
            {
                if ((float)_in.GetValue(__instance) > 0f && _origIn > 0f) _in.SetValue(__instance, _origIn);
                _out.SetValue(__instance, _origOut);
                _secs.SetValue(__instance, _origSecs);
                return;
            }
            if ((float)_in.GetValue(__instance) <= 0f) return;
            _in.SetValue(__instance, Tight() ? EnterAt : 0.18f);
            _out.SetValue(__instance, LeaveAt);
            bool holding = sq >= 0.999f && Time.time - _lastIn < Hold.Value;
            _secs.SetValue(__instance, holding ? 1e6f : _leaving || sq >= 0.999f ? BackSeconds : Speed.Value);
        }
        catch (Exception e) { Plugin.Out.LogDebug("[Door] " + e.Message); }
    }

    static bool _tight;
    static float _nextTight;

    static bool Tight()
    {
        if (Time.time < _nextTight) return _tight;
        _nextTight = Time.time + 0.1f;
        _tight = false;
        try
        {
            var pn = Game.Pn();
            if (pn == null) return false;
            var t = pn.transform;
            var at = t.position + Vector3.up * 1.2f;
            var right = t.right; right.y = 0f; right.Normalize();
            var me = t.root;
            _tight = Game.Cast(at, right, 0.95f, me, out _) && Game.Cast(at, -right, 0.95f, me, out _);
            if (!_tight)
                foreach (var d in Map.Doors)
                {
                    var v = d.C - t.position;
                    if (Mathf.Abs(v.y) > 1.8f) continue;
                    v.y = 0f;
                    if (v.sqrMagnitude < 2.25f) { _tight = true; break; }
                }
        }
        catch { }
        return _tight;
    }

    public static void After(object __instance, bool first)
    {
        if (!first) return;
        try
        {
            float sq = (float)_sq.GetValue(__instance);
            if (sq >= 0.999f && (float)_target.GetValue(__instance) >= 0.999f) _lastIn = Time.time;
            _leaving = sq < _before;
        }
        catch { }
    }
}
