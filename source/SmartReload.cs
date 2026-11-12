using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

// COD-style handling for your own weapon, whatever key Reload is bound to:
//  - after any reload, if no round is chambered (gun was empty) the mod racks one in
//  - if the gun jammed, the next reload also racks to clear the dud
//  - a reload with a round already chambered stays a normal reload
// It watches the weapon's own reload state instead of one input method, so it works
// whichever way the game starts the reload.
internal static class SmartReload
{
    internal static ConfigEntry<float> Speed;
    static float _blockUntil = -1f;
    static readonly List<(object anim, PropertyInfo p)> _sped = new();

    static PropertyInfo _pLocal;
    static WeaponV3 _w;
    static float _nextFind, _rackAt = -1f, _verifyAt = -1f, _lastDone = -10f;
    static bool _wasReloading, _jammed, _ours, _triedMods, _needRack, _sawReload;
    static float _lastPress = -10f, _checkAt = -1f, _rackFailedAt = -100f;
    static readonly Dictionary<string, int> _seen = new();

    internal static void Init(ConfigFile cfg, Harmony h)
    {
        Speed = cfg.Bind("Handling", "ReloadSpeed", 100f, new ConfigDescription("Reload speed, percent.", new AcceptableValueRange<float>(100f, 250f)));
        Patch(h, typeof(WeaponV3), "Reload", null, nameof(ReloadPrefix));
        // The game's own Chamber key (also on C) racked a live round out on its own: C now goes through the mod only.
        Patch(h, typeof(WeaponV3), "ManualChamber", null, nameof(ChamberPrefix));
        Patch(h, typeof(WeaponMods), "ManualChamber", null, nameof(ChamberPrefix));
        Watch(h, typeof(WeaponV3), "CMD_Reload", "RPC_Reload", "CMD_Manual_Chamber");
        Watch(h, typeof(WeaponMods), "CMD_ClientReloadMag", "I_ClientReloadMag");
        Watch(h, typeof(FPSAnimationHelper), "PlayReloadAnimation", "ChamberRound", "FinishingChambering");
        Patch(h, typeof(FPSAnimationHelper), "EndReload", nameof(EndReloadPostfix));
        Patch(h, typeof(WeaponV3), "CMD_malfunctionMisfire", nameof(MisfirePostfix));
        Patch(h, typeof(WeaponV3), "RPC_malfunctionMisfire", nameof(MisfirePostfix));
        Plugin.Out.LogInfo("[Reload] smart reload ready (" + "on" + ")");
    }

    static void Patch(Harmony h, Type t, string m, string post, string pre = null)
    {
        try
        {
            var mi = AccessTools.Method(t, m);
            if (mi != null) h.Patch(mi,
                prefix: pre == null ? null : new HarmonyMethod(typeof(SmartReload), pre),
                postfix: post == null ? null : new HarmonyMethod(typeof(SmartReload), post));
            else Plugin.Out.LogWarning("[Reload] missing " + t.Name + "." + m);
        }
        catch (Exception e) { Plugin.Out.LogWarning("[Reload] patch " + m + ": " + e.Message); }
    }

    // Logs the first few calls of each method, so the log shows which ones the game really uses.
    static void Watch(Harmony h, Type t, params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                var mi = AccessTools.Method(t, n);
                if (mi == null) continue;
                var post = new HarmonyMethod(typeof(SmartReload), nameof(SeenPostfix));
                h.Patch(mi, postfix: post);
            }
            catch (Exception e) { Plugin.Out.LogDebug(n + ": " + e.Message); }
        }
    }

    public static void SeenPostfix(MethodBase __originalMethod)
    {
        try
        {
            var key = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
            _seen.TryGetValue(key, out var c);
            if (c < 4) { _seen[key] = c + 1; Plugin.Out.LogInfo("[Reload] game called " + key); }
        }
        catch { }
    }

    static bool Mine(WeaponV3 w)
    {
        try
        {
            if (w == null || !w.isEquiped) return false;
            var pn = w.playerNetworking;
            if (pn == null) return false;
            _pLocal ??= AccessTools.Property(pn.GetType(), "isLocalPlayer");
            return _pLocal == null || (bool)_pLocal.GetValue(pn);
        }
        catch { return false; }
    }

    static bool IsLocal(WeaponV3 w)
    {
        try
        {
            var pn = PlayerMaster.MyPlayerMaster?.PlayerSpawnedObject;
            return pn != null && w.playerNetworking != null && w.playerNetworking.Pointer == pn.Pointer;
        }
        catch { return false; }
    }

    static WeaponV3 Local()
    {
        try { if (_w != null && _w.isEquiped) return _w; } catch { _w = null; }
        if (Time.unscaledTime < _nextFind) return null;
        _nextFind = Time.unscaledTime + 0.5f;
        _w = null;
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<WeaponV3>();
            if (all != null)
                foreach (var w in all)
                    if (IsLocal(w) && w.isEquiped) { _w = w; Plugin.Out.LogInfo("[Reload] tracking your weapon " + w.gameObject.name); break; }
        }
        catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        return _w;
    }

    // A key press that would make the game rack on its own: blocked, the mod decides (see ReloadPrefix).
    public static bool ChamberPrefix()
    {
        if (_ours) return true;
        bool fromKey = Input.anyKeyDown || Time.unscaledTime - _lastPress < 0.4f;
        if (!fromKey) return true;
        Plugin.Out.LogInfo("[Reload] game's chamber key blocked: C decides");
        return false;
    }

    static string Describe(WeaponV3 w, GunFunction gf, Magazine mag)
    {
        string m = gf != null && gf.isTubeFed ? "tube" : mag != null && gf != null && gf.magazineInserted ? mag.ammoAmount + "/" + mag.ammoAmountMax : "no mag";
        bool locked = false; try { locked = gf != null && gf.BoltLockedBack; } catch { }
        return w.gameObject.name.Replace("(Clone)", "") + ", " + m + ", chambered " + w.roundInChamber + (locked ? ", slide locked" : "") + (_jammed ? ", JAMMED" : "");
    }

    // Your reload key: decide what the gun actually needs.
    public static bool ReloadPrefix(WeaponV3 __instance)
    {
        try
        {
            if (_ours || __instance == null) return true;
            _w = __instance;
            var gf = __instance.gunFunction;
            var mag = gf?.currentMagazine;
            bool magHasAmmo = gf != null && gf.magazineInserted && mag != null && mag.ammoAmount > 0;
            // Just racked, or racking right now: swallow the same key press so it does not also reload.
            if (Time.unscaledTime < _blockUntil || __instance.isChambering) return false;
            _lastPress = Time.unscaledTime;
            bool tube = gf != null && gf.isTubeFed;
            bool magIn = gf != null && gf.magazineInserted && mag != null;
            bool magFull = !tube && magIn && mag.ammoAmount >= mag.ammoAmountMax;
            Plugin.Out.LogInfo("[Reload] C pressed: " + Describe(__instance, gf, mag));
            // 1. Jammed: clear the jam only.
            if (_jammed)
            {
                Rack(__instance, "jammed: clearing it, no reload");
                _blockUntil = Time.unscaledTime + 2f;
                return false;
            }
            // 2. Nothing chambered but rounds to feed (bolt-action after a shot, pump shotgun, fresh gun): chamber only.
            //    A shotgun's tube count isn't readable, so it tries a pump first; if that didn't chamber anything, the tube is empty and C reloads.
            bool rounds = tube ? Time.unscaledTime - _rackFailedAt > 6f : magIn && mag.ammoAmount > 0;
            if (!__instance.roundInChamber && rounds)
            {
                Rack(__instance, "chamber empty, rounds to feed: chamber only");
                _blockUntil = Time.unscaledTime + 1.2f;
                return false;
            }
            // 3. Full mag and chambered: nothing to do.
            if (magFull)
            {
                Plugin.Out.LogInfo("[Reload] full mag, round chambered: nothing to do");
                return false;
            }
            // 4. Less than full: reload. Chamber after only if the gun was empty.
            //    Shotgun (tube): load shells; pump after only if the chamber is empty.
            _needRack = !__instance.roundInChamber;
            _sawReload = false;
            _checkAt = Time.unscaledTime + 0.6f; // if the game doesn't start a reload (e.g. tube already full), still chamber if needed
            Plugin.Out.LogInfo("[Reload] reload" + (_needRack ? ", then chamber" : ", no chamber"));
        }
        catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        return true;
    }

    public static void MisfirePostfix(WeaponV3 __instance)
    {
        try
        {
            if (__instance != null && (__instance == _w || IsLocal(__instance)) && !_jammed) { _jammed = true; Plugin.Out.LogInfo("[Reload] jam detected: press C to clear it"); }
        }
        catch { }
    }

    public static void EndReloadPostfix() => ReloadDone("end of reload animation");

    static void ReloadDone(string why)
    {
        if (Time.unscaledTime - _lastDone < 0.5f) return;
        _lastDone = Time.unscaledTime;
        _rackAt = Time.unscaledTime + 0.2f;
        Plugin.Out.LogInfo("[Reload] reload finished (" + why + ")");
    }

    internal static void Tick()
    {
        var w = Local();
        if (w == null) return;
        try
        {
            var gf = w.gunFunction;
            bool reloading = gf != null && gf.isReloading;
            if (reloading) _sawReload = true;
            if (_checkAt > 0f && Time.unscaledTime >= _checkAt)
            {
                _checkAt = -1f;
                if (!_sawReload && !reloading && _needRack) ReloadDone("no reload happened");
            }
            if (!_wasReloading && reloading) SpeedUp(w);
            if (_wasReloading && !reloading) { SlowDown(); ReloadDone("reload state ended"); }
            _wasReloading = reloading;

            if (_rackAt > 0f && Time.unscaledTime >= _rackAt && !reloading)
            {
                _rackAt = -1f;
                if (w.isChambering) { _rackAt = Time.unscaledTime + 0.2f; return; }
                var mag = gf?.currentMagazine;
                bool tube = gf != null && gf.isTubeFed;
                bool hasAmmo = tube || (gf != null && gf.magazineInserted && mag != null && mag.ammoAmount > 0);
                if (!hasAmmo) { _needRack = false; Plugin.Out.LogInfo("[Reload] no loaded mag (out of magazines?), nothing to chamber"); return; }
                if (!_needRack && !_jammed) return; // partial-mag reload: no chambering
                _needRack = false;
                if (w.roundInChamber && !_jammed) { Plugin.Out.LogInfo("[Reload] round already chambered: nothing to do"); return; }
                Rack(w, _jammed ? "clearing jam" : "empty reload");
            }

            if (_verifyAt > 0f && Time.unscaledTime >= _verifyAt && !w.isChambering)
            {
                _verifyAt = -1f;
                if (w.roundInChamber) { Plugin.Out.LogInfo("[Reload] chambered OK"); _jammed = false; }
                else if (!_triedMods)
                {
                    _triedMods = true;
                    Plugin.Out.LogInfo("[Reload] still empty, trying the weapon's direct rack");
                    _ours = true;
                    try { w.ManualChamber(); } catch (Exception e) { Plugin.Out.LogWarning("[Reload] " + e.Message); }
                    finally { _ours = false; }
                    _verifyAt = Time.unscaledTime + 1.5f;
                }
                else { _rackFailedAt = Time.unscaledTime; Plugin.Out.LogInfo("[Reload] nothing chambered: out of rounds, next C reloads"); }
            }
        }
        catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
    }

    static void SpeedUp(WeaponV3 w)
    {
        SlowDown();
        float k = Speed.Value / 100f;
        if (k <= 1.01f) return;
        foreach (var holder in new object[] { w.fpsAnimationHelper, w.weaponAnimation })
        {
            try
            {
                if (holder == null) continue;
                var anim = AccessTools.Property(holder.GetType(), "animator")?.GetValue(holder);
                var sp = anim == null ? null : AccessTools.Property(anim.GetType(), "speed");
                if (sp == null) continue;
                sp.SetValue(anim, k);
                _sped.Add((anim, sp));
            }
            catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        }
        if (_sped.Count > 0) Plugin.Out.LogInfo("[Reload] reload speed x" + k.ToString("0.00"));
    }

    static void SlowDown()
    {
        foreach (var (anim, p) in _sped) try { p.SetValue(anim, 1f); } catch { }
        _sped.Clear();
    }

    static void Rack(WeaponV3 w, string why)
    {
        _ours = true; _triedMods = false;
        try
        {
            if (w.mods != null) w.mods.ManualChamber(); else w.ManualChamber();
            Plugin.Out.LogInfo("[Reload] " + why + ": racking");
        }
        catch (Exception e) { Plugin.Out.LogWarning("[Reload] rack failed: " + e.Message); }
        finally { _ours = false; }
        _verifyAt = Time.unscaledTime + 1.5f;
    }
}
