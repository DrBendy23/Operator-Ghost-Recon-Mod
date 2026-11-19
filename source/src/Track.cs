using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

internal static class Track
{
    internal static readonly List<Health> Healths = new List<Health>();
    internal static readonly List<WeaponV3> Weapons = new List<WeaponV3>();
    internal static readonly List<IRLaserLight> Lasers = new List<IRLaserLight>();
    internal static readonly List<BrainAI> Brains = new List<BrainAI>();

    static readonly HashSet<IntPtr> _known = new HashSet<IntPtr>();
    static bool _hookHealth, _hookWeapon, _hookLaser, _hookBrain;
    static string _scene;
    static float _nextCheck, _nextSweep;

    internal static void Init(HarmonyLib.Harmony h)
    {
        _hookHealth = Hook(h, typeof(Health), nameof(HealthAwake));
        _hookWeapon = Hook(h, typeof(WeaponV3), nameof(WeaponAwake));
        _hookLaser = Hook(h, typeof(IRLaserLight), nameof(LaserAwake));
        _hookBrain = false;
        Game.Log.LogInfo("[Track] hooks: health " + _hookHealth + ", weapon " + _hookWeapon + ", laser " + _hookLaser + ", ai " + _hookBrain);
    }

    static bool Hook(HarmonyLib.Harmony h, Type type, string post)
    {
        try
        {
            var m = AccessTools.Method(type, "Awake");
            if (m == null) return false;
            h.Patch(m, postfix: new HarmonyMethod(typeof(Track), post));
            return true;
        }
        catch (Exception e) { Game.Log.LogWarning("[Track] " + type.Name + ": " + e.Message); return false; }
    }

    static int _hitHealth, _hitWeapon, _hitLaser;
    static float _nextLocal, _nextWide, _nextBrains;

    public static void HealthAwake(Health __instance) { _hitHealth++; OnHealth(__instance); }
    public static void WeaponAwake(WeaponV3 __instance) { _hitWeapon++; OnWeapon(__instance); }
    public static void LaserAwake(IRLaserLight __instance) { _hitLaser++; OnLaser(__instance); }

    static void Local()
    {
        try
        {
            var pn = Game.Pn();
            if (pn == null) return;
            var root = pn.transform.root;
            foreach (var o in root.GetComponentsInChildren<WeaponV3>(true)) OnWeapon(o);
            foreach (var o in root.GetComponentsInChildren<IRLaserLight>(true)) OnLaser(o);
            foreach (var o in root.GetComponentsInChildren<Health>(true)) OnHealth(o);
        }
        catch (Exception e) { Game.Log.LogDebug("[Track] local: " + e.Message); }
    }

    public static void OnHealth(Health __instance) { try { if (__instance != null && _known.Add(__instance.Pointer)) Healths.Add(__instance); } catch { } }
    public static void OnWeapon(WeaponV3 __instance) { try { if (__instance != null && _known.Add(__instance.Pointer)) Weapons.Add(__instance); } catch { } }
    public static void OnLaser(IRLaserLight __instance) { try { if (__instance != null && _known.Add(__instance.Pointer)) Lasers.Add(__instance); } catch { } }
    public static void OnBrain(BrainAI __instance) { try { if (__instance != null && _known.Add(__instance.Pointer)) Brains.Add(__instance); } catch { } }

    static void Prune<T>(List<T> list) where T : UnityEngine.Object
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            bool gone = false;
            try { gone = list[i] == null; } catch { gone = true; }
            if (gone) list.RemoveAt(i);
        }
    }

    static void Sweep(bool all)
    {
        try
        {
            if (all || !_hookHealth || _hitHealth == 0) foreach (var o in UnityEngine.Object.FindObjectsByType<Health>(FindObjectsSortMode.None)) OnHealth(o);
            if (all || !_hookWeapon || _hitWeapon == 0) foreach (var o in UnityEngine.Object.FindObjectsByType<WeaponV3>(FindObjectsSortMode.None)) OnWeapon(o);
            if (all || !_hookLaser || _hitLaser == 0) foreach (var o in UnityEngine.Object.FindObjectsByType<IRLaserLight>(FindObjectsSortMode.None)) OnLaser(o);
            foreach (var hp in Healths) { try { if (hp != null && hp.brainAI != null) OnBrain(hp.brainAI); } catch { } }
        }
        catch (Exception e) { Game.Log.LogDebug("[Track] sweep: " + e.Message); }
    }

    internal static void Tick()
    {
        float now = Time.unscaledTime;
        if (now < _nextCheck) return;
        _nextCheck = now + 1f;
        string scene = "";
        try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; } catch { }
        Prune(Healths); Prune(Weapons); Prune(Lasers); Prune(Brains);
        if (scene != _scene)
        {
            _scene = scene;
            _known.Clear();
            foreach (var o in Healths) _known.Add(o.Pointer);
            foreach (var o in Weapons) _known.Add(o.Pointer);
            foreach (var o in Lasers) _known.Add(o.Pointer);
            foreach (var o in Brains) _known.Add(o.Pointer);
            Sweep(true);
            _nextSweep = now + 3f; _nextWide = now + 10f;
            Game.Log.LogInfo("[Track] hook calls so far: health " + _hitHealth + ", weapon " + _hitWeapon + ", laser " + _hitLaser + "");
            Game.Log.LogInfo("[Track] scene " + scene + ": " + Healths.Count + " bodies, " + Weapons.Count + " weapons, " + Lasers.Count + " lasers, " + Brains.Count + " ai");
            return;
        }
        if (now >= _nextLocal) { _nextLocal = now + 2f; Local(); }
        if (now >= _nextBrains) { _nextBrains = now + 2f; foreach (var hp in Healths) { try { if (hp != null && hp.brainAI != null) OnBrain(hp.brainAI); } catch { } } }
        if ((_hitHealth == 0 || _hitWeapon == 0) && now >= _nextWide) { _nextWide = now + 10f; Sweep(false); }
        if ((!_hookHealth || !_hookWeapon) && now >= _nextSweep) { _nextSweep = now + 3f; Sweep(false); }
    }
}
