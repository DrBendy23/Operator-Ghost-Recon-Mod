using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

// Small shared helpers: logging, the third-person camera (if the BASS mod is installed), your player and its controllers.
internal static class Game
{
    internal static ManualLogSource Log;

    static System.Reflection.FieldInfo _fThird, _fCam; static bool _looked;
    static void Look()
    {
        if (_looked) return; _looked = true;
        var g = AccessTools.TypeByName("OperatorTPS.Game");
        if (g != null) { _fThird = AccessTools.Field(g, "ThirdPerson"); _fCam = AccessTools.Field(g, "TpCamera"); }
    }
    internal static bool ThirdPerson { get { Look(); try { return _fThird != null && (bool)_fThird.GetValue(null); } catch { return false; } } }
    internal static Camera TpCamera { get { Look(); try { return _fCam?.GetValue(null) as Camera; } catch { return null; } } }
    internal static Camera ViewCamera => ThirdPerson && TpCamera != null ? TpCamera : Camera.main;

    internal static PlayerNetworking Pn() { try { return PlayerMaster.MyPlayerMaster?.PlayerSpawnedObject; } catch { return null; } }

    static MasterController _mc; static IntPtr _mcOwner;
    internal static MasterController MasterOf(PlayerNetworking pn)
    {
        if (pn == null) return null;
        if (_mc != null && _mcOwner == pn.Pointer) return _mc;
        _mc = null; _mcOwner = pn.Pointer;
        try
        {
            foreach (var mc in UnityEngine.Object.FindObjectsOfType<MasterController>())
                if (mc != null && mc.playerNetworking != null && mc.playerNetworking.Pointer == pn.Pointer) { _mc = mc; break; }
        }
        catch { }
        return _mc;
    }
    internal static CMF.CameraController CamOf(PlayerNetworking pn) { var mc = MasterOf(pn); try { return mc != null ? mc.cameraController : null; } catch { return null; } }

    // Is a drone in the air? Works whether the drone lives in this mod or in the separate Drone Operator mod.
    static System.Reflection.PropertyInfo _flying; static bool _flyLooked;
    internal static bool DroneFlying
    {
        get
        {
            if (!_flyLooked) { _flyLooked = true; var t = AccessTools.TypeByName("GhostReconView.Drone"); if (t != null) _flying = AccessTools.Property(t, "Flying"); }
            try { return _flying != null && (bool)_flying.GetValue(null); } catch { return false; }
        }
    }
}
