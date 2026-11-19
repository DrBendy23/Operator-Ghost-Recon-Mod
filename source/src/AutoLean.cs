using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

internal static class AutoLean
{
    internal static ConfigEntry<bool> On;
    internal static ConfigEntry<float> Reach;

    static int _want;
    static float _since; static int _raw;

    internal static void Init(ConfigFile cfg)
    {
        On = cfg.Bind("Movement", "AutoLean", true, "Lean around cover by yourself when you aim past its edge.");
        Reach = cfg.Bind("Movement", "AutoLeanReachV3", 1.8f, new ConfigDescription("Auto lean looks ahead from your eyes and from each shoulder. If your eyes see open space but one shoulder's view hits a wall within this distance, you are at the edge of cover and you lean to that side. Bigger = leans from farther away from the wall.", new AcceptableValueRange<float>(0.5f, 4f)));
    }

    static bool Wall(Vector3 from, Vector3 dir, float dist, Transform me)
    {
        try
        {
            return Game.Cast(from, dir, dist, me, out _);
        }
        catch { return false; }
    }

    static bool KeyHeld(InputKey k)
    {
        try
        {
            var kb = InputLayer.Keybinds;
            if (kb != null && kb.ContainsKey(k)) { var b = kb[k].KeyBind; return Input.GetKey(b.PrimaryKey) || (b.PreliminaryKey != KeyCode.None && Input.GetKey(b.PreliminaryKey)); }
        }
        catch { }
        return false;
    }

    static Health _health; static IntPtr _healthOwner;
    static Vector3 _lastPos; static bool _hasPos; static float _speed, _setAt, _vel;

    static float EyeHeight(PlayerNetworking pn, Transform me)
    {
        try
        {
            if (_health == null || _healthOwner != pn.Pointer)
            {
                _healthOwner = pn.Pointer;
                _health = pn.GetComponent<Health>();
                if (_health == null) _health = pn.GetComponentInParent<Health>();
                if (_health == null) _health = pn.GetComponentInChildren<Health>(true);
            }
            var head = _health != null ? _health.Head : null;
            if (head != null) return Mathf.Clamp(head.position.y - me.position.y, 0.55f, 1.8f);
        }
        catch { }
        return 1.55f;
    }

    internal static void Tick()
    {
        int raw = 0;
        var pn = Game.DroneFlying || !On.Value || Cursor.lockState != CursorLockMode.Locked ? null : PlayerMaster.MyPlayerMaster?.PlayerSpawnedObject;
        var mc = Game.MasterOf(pn);
        float now = Time.unscaledTime;
        if (pn != null && mc != null && !KeyHeld(InputKey.LeanLeft) && !KeyHeld(InputKey.LeanRight) && !KeyHeld(InputKey.FreeLean))
        {
            var me = pn.transform;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 0.001f);
            if (_hasPos) { var d = me.position - _lastPos; d.y = 0f; _speed = Mathf.Lerp(_speed, d.magnitude / dt, 0.2f); }
            _lastPos = me.position; _hasPos = true;
            bool ads = false; try { ads = pn.isADS; } catch { }
            if (ads || _speed < 3.6f)
            {
                var cc = Game.CamOf(pn);
                Vector3 aim = me.forward; try { if (cc != null) aim = cc.GetAimingDirection(); } catch { }
                var flat = Vector3.ProjectOnPlane(aim, Vector3.up).normalized;
                var right = Vector3.Cross(Vector3.up, flat);
                var eyes = me.position + Vector3.up * EyeHeight(pn, me);
                float r = Reach.Value;
                bool shL = Wall(eyes - right * 0.45f, aim, r, me), shR = Wall(eyes + right * 0.45f, aim, r, me);
                if (!Wall(eyes, aim, 2.5f, me))
                {
                    if (shL && !shR && !Wall(eyes + right * 0.8f, aim, r + 0.3f, me)) raw = -1;
                    else if (shR && !shL && !Wall(eyes - right * 0.8f, aim, r + 0.3f, me)) raw = 1;
                }
                else if (Wall(eyes, aim, 1.4f, me))
                {
                    if (shL && !shR && !Wall(eyes + right * 0.45f, aim, 3.5f, me) && !Wall(eyes + right * 0.8f, aim, 3.5f, me)) raw = -1;
                    else if (shR && !shL && !Wall(eyes - right * 0.45f, aim, 3.5f, me) && !Wall(eyes - right * 0.8f, aim, 3.5f, me)) raw = 1;
                }
            }
        }
        else _hasPos = false;
        if (raw != _raw) { _raw = raw; _since = now; }
        float held = now - _since;
        if (raw == _want) return;
        bool flip = raw != 0 && _want != 0;
        if (flip ? held > 0.15f : raw != 0 ? held > 0.08f : (held > 0.4f && now - _setAt > 0.6f)) Set(raw, mc);
    }

    static void Set(int w, MasterController mc)
    {
        _want = w; _setAt = Time.unscaledTime;
        Plugin.Out.LogInfo("[Lean] " + (w < 0 ? "left" : w > 0 ? "right" : "off"));
    }

    internal static void InitPatches(HarmonyLib.Harmony h)
    {
        try
        {
            var m = AccessTools.Method(typeof(MasterController), "Update");
            if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(AutoLean), nameof(AfterUpdate)));
        }
        catch (Exception e) { Plugin.Out.LogWarning("[Lean] patch: " + e.Message); }
    }

    static bool _logged;

    public static void AfterUpdate(MasterController __instance)
    {
        if (_want == 0 && !Game.DroneFlying) return;
        try
        {
            if (__instance == null || __instance.playerNetworking == null || __instance.playerNetworking.Pointer != PlayerMaster.MyPlayerMaster?.PlayerSpawnedObject?.Pointer) return;
            var pl = __instance.playerLean; if (pl == null) return;
            if (Game.DroneFlying) { pl.output = Mathf.MoveTowards(pl.output, 0f, Mathf.Abs(pl.outputMax - pl.outputMin) * 3.5f * Time.deltaTime); return; }
            float target = _want > 0 ? pl.outputMin : pl.outputMax;
            if (!_logged) { _logged = true; Plugin.Out.LogInfo("[Lean] output range " + pl.outputMin + " .. " + pl.outputMax + ", now " + pl.output); }
            pl.output = Mathf.SmoothDamp(pl.output, target, ref _vel, 0.11f, Mathf.Abs(pl.outputMax - pl.outputMin) * 6f, Mathf.Max(Time.deltaTime, 0.001f));
        }
        catch (Exception e) { Plugin.Out.LogDebug("[Lean] " + e.Message); }
    }
}
