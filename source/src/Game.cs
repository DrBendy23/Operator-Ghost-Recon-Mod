using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

internal static class Game
{
    internal static ManualLogSource Log;

    static TpToggle _tp; static IntPtr _tpOwner;
    static TpToggle Tp()
    {
        var pn = Pn();
        if (pn == null) { _tp = null; _tpOwner = IntPtr.Zero; return null; }
        if (_tp != null && _tpOwner == pn.Pointer) return _tp;
        _tpOwner = pn.Pointer;
        try { _tp = pn.GetComponentInChildren<TpToggle>(true); } catch { _tp = null; }
        return _tp;
    }
    internal static bool ThirdPerson { get { try { var t = Tp(); return t != null && t.inThirdPerson; } catch { return false; } } }
    internal static Camera TpCamera { get { try { var t = Tp(); return t != null ? t.TPCamera : null; } catch { return null; } } }
    internal static Camera ViewCamera => ThirdPerson && TpCamera != null ? TpCamera : Camera.main;

    internal static void Label(Rect r, string text)
    {
        Font5.Draw(r.x, r.y + 3f, text, 2f);
    }

    internal static bool Cast(Vector3 origin, Vector3 dir, float max, Transform me, out Vector3 point)
    {
        point = origin + dir * max;
        var hits = Physics.RaycastAll(origin, dir, max, -5, QueryTriggerInteraction.Ignore);
        if (hits == null) return false;
        float best = float.MaxValue; bool found = false;
        for (int i = 0; i < hits.Length; i++)
        {
            var h = hits[i];
            if (h.distance < 0.05f || h.distance >= best) continue;
            var t = h.transform;
            if (t == null || (me != null && (t == me || t.IsChildOf(me)))) continue;
            best = h.distance; point = h.point; found = true;
        }
        return found;
    }

    internal static int MyTeam()
    {
        try { var t = PlayerMaster.MyPlayerMaster?.MyTeamIdentifier; return t != null ? t.TeamID : int.MinValue; } catch { return int.MinValue; }
    }

    internal static Transform BodyOf(Health h)
    {
        var t = h.transform;
        string name = null;
        try { var b = h.brainAI; if (b != null) name = b.myName; } catch { }
        if (string.IsNullOrEmpty(name)) return t;
        for (var p = t; p != null; p = p.parent)
            if (p.name == name) return p;
        return t;
    }

    internal static Vector3 PlaceOf(Health h)
    {
        try
        {
            var b = h.brainAI;
            if (b != null) { var a = b.agent; if (a != null && a.entityExists) return a.position; return b.transform.position; }
        }
        catch { }
        return h.transform.position;
    }

    internal static PlayerNetworking Pn() { try { return PlayerMaster.MyPlayerMaster?.PlayerSpawnedObject; } catch { return null; } }

    static MasterController _mc; static IntPtr _mcOwner;
    internal static MasterController MasterOf(PlayerNetworking pn)
    {
        if (pn == null) return null;
        if (_mc != null && _mcOwner == pn.Pointer) return _mc;
        _mc = null; _mcOwner = pn.Pointer;
        try
        {
            foreach (var mc in UnityEngine.Object.FindObjectsByType<MasterController>(FindObjectsSortMode.None))
                if (mc != null && mc.playerNetworking != null && mc.playerNetworking.Pointer == pn.Pointer) { _mc = mc; break; }
        }
        catch { }
        return _mc;
    }
    internal static CMF.CameraController CamOf(PlayerNetworking pn) { var mc = MasterOf(pn); try { return mc != null ? mc.cameraController : null; } catch { return null; } }

    static System.Reflection.PropertyInfo _flying; static bool _flyLooked;
    internal static bool DroneFlying
    {
        get
        {
            if (!_flyLooked) { _flyLooked = true; var t = AccessTools.TypeByName("GhostReconView.Drone"); if (t != null) _flying = AccessTools.Property(t, "Flying"); }
            try { return _flying != null && (bool)_flying.GetValue(null); } catch { return false; }
        }
    }

    internal static Transform Muzzle()
    {
        try { return Pn()?.c_activeWeapon?.gunFunction?.muzzleEnd; } catch { return null; }
    }
    internal static bool Barrel(Transform m, Vector3 aim, out Vector3 dir)
    {
        dir = aim; float best = -2f; var r = m.rotation;
        foreach (var a in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left, Vector3.up, Vector3.down })
        { var d = r * a; float dot = Vector3.Dot(d, aim); if (dot > best) { best = dot; dir = d; } }
        return best >= 0.6f;
    }
}
