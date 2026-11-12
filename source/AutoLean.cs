using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

// Auto lean (CQB): when you aim past the edge of cover (your eyes see the room, one shoulder is still behind the wall),
// your operator leans to that side by himself. It flips the game's own lean switches (MasterController.toggleLeanLeft/Right),
// the same ones your lean buttons flip, so the game animates and syncs it. Your own lean buttons always win.
internal static class AutoLean
{
    internal static ConfigEntry<bool> On;
    internal static ConfigEntry<float> Reach;

    static int _want;            // -1 left, 0 none, +1 right
    static float _since; static int _raw;
    static System.Reflection.MethodInfo _cast;

    internal static void Init(ConfigFile cfg)
    {
        On = cfg.Bind("Movement", "AutoLean", true, "Lean around cover by yourself when you aim past its edge.");
        Reach = cfg.Bind("Movement", "AutoLeanReachV3", 1.8f, new ConfigDescription("Auto lean looks ahead from your eyes and from each shoulder. If your eyes see open space but one shoulder's view hits a wall within this distance, you are at the edge of cover and you lean to that side. Bigger = leans from farther away from the wall.", new AcceptableValueRange<float>(0.5f, 4f)));
    }

    static bool Wall(Vector3 from, Vector3 dir, float dist, Transform me)
    {
        try
        {
            _cast ??= AccessTools.Method(AccessTools.TypeByName("OperatorTPS.Physics2"), "CastIgnoringPlayer");
            if (_cast == null) return false;
            var args = new object[] { from, dir, dist, me, null };
            return (bool)_cast.Invoke(null, args);
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

    internal static void Tick()
    {
        int raw = 0;
        var pn = Game.DroneFlying || !On.Value || Cursor.lockState != CursorLockMode.Locked ? null : PlayerMaster.MyPlayerMaster?.PlayerSpawnedObject;
        var mc = Game.MasterOf(pn);
        if (pn != null && mc != null && !KeyHeld(InputKey.LeanLeft) && !KeyHeld(InputKey.LeanRight) && !KeyHeld(InputKey.FreeLean))
        {
            var me = pn.transform;
            var cc = Game.CamOf(pn);
            Vector3 aim = me.forward; try { if (cc != null) aim = cc.GetAimingDirection(); } catch { }
            var flat = Vector3.ProjectOnPlane(aim, Vector3.up).normalized;
            var right = Vector3.Cross(Vector3.up, flat);
            var eyes = me.position + Vector3.up * 1.55f;
            float r = Reach.Value;
            if (!Wall(eyes, aim, 2.5f, me))
            {
                // cover side: shoulder blocked close. open side: shoulder AND a wider point clear (so a door frame you walk through does nothing)
                bool shL = Wall(eyes - right * 0.45f, aim, r, me), shR = Wall(eyes + right * 0.45f, aim, r, me);
                if (shL && !shR && !Wall(eyes + right * 0.8f, aim, r + 0.3f, me)) raw = -1;
                else if (shR && !shL && !Wall(eyes - right * 0.8f, aim, r + 0.3f, me)) raw = 1;
            }
        }
        if (raw != _raw) { _raw = raw; _since = Time.unscaledTime; }
        float held = Time.unscaledTime - _since;
        bool flip = raw != 0 && _want != 0 && raw != _want;                      // other side: switch at once
        if (raw != _want && (flip || (raw != 0 ? held > 0.02f : held > 0.25f))) Set(raw, mc);
    }

    static void Set(int w, MasterController mc)
    {
        _want = w;
        Plugin.Out.LogInfo("[Lean] " + (w < 0 ? "left" : w > 0 ? "right" : "off"));
    }

    internal static void InitPatches(Harmony h)
    {
        try
        {
            var m = AccessTools.Method(typeof(MasterController), "Update");
            if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(AutoLean), nameof(AfterUpdate)));
        }
        catch (Exception e) { Plugin.Out.LogWarning("[Lean] patch: " + e.Message); }
    }

    static bool _logged;
    // Right after the game computed this frame's lean, push ours in: the animator and the network sync (CMD_UpdateValues) read it from here.
    public static void AfterUpdate(MasterController __instance)
    {
        if (_want == 0 && !Game.DroneFlying) return;
        try
        {
            if (__instance == null || __instance.playerNetworking == null || __instance.playerNetworking.Pointer != PlayerMaster.MyPlayerMaster?.PlayerSpawnedObject?.Pointer) return;
            var pl = __instance.playerLean; if (pl == null) return;
            if (Game.DroneFlying) { pl.output = Mathf.MoveTowards(pl.output, 0f, Mathf.Abs(pl.outputMax - pl.outputMin) * 3.5f * Time.deltaTime); return; }
            float target = _want > 0 ? pl.outputMin : pl.outputMax;   // game: outputMax = left side
            if (!_logged) { _logged = true; Plugin.Out.LogInfo("[Lean] output range " + pl.outputMin + " .. " + pl.outputMax + ", now " + pl.output); }
            pl.output = Mathf.MoveTowards(pl.output, target, Mathf.Abs(pl.outputMax - pl.outputMin) * 5.5f * Time.deltaTime);
        }
        catch (Exception e) { Plugin.Out.LogDebug("[Lean] " + e.Message); }
    }
}
