using System;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Events;

namespace GhostReconView;

// Drone Operator: recon + attack FPV drones for OPERATOR. Standalone; works with or without Ghost Recon View.
[BepInPlugin(Guid, "Drone Operator", "1.0")]
[BepInDependency("local.operator.modcontrols", BepInDependency.DependencyFlags.SoftDependency)]
public class DronePlugin : BasePlugin
{
    public const string Guid = "droneoperator.operator";
    static UnityAction _beforeRender;

    public override void Load()
    {
        Game.Log = Log;
        var harmony = new Harmony(Guid);
        try { Drone.Init(Config); Drone.InitPatches(harmony); } catch (Exception e) { Log.LogWarning("[Drone] " + e.Message); }
        try { Menu(); } catch { Log.LogInfo("[Drone] settings menu not installed; edit BepInEx/config/" + Guid + ".cfg instead"); }
        ClassInjector.RegisterTypeInIl2Cpp<DroneBehaviour>();
        AddComponent<DroneBehaviour>();
        _beforeRender = DelegateSupport.ConvertDelegate<UnityAction>(new Action(() => { try { Drone.BeforeRender(); } catch { } }));
        Application.add_onBeforeRender(_beforeRender);
        Log.LogInfo("Drone Operator loaded.");
    }

    // Only runs if the OPERATOR mod settings menu (OperatorModControls) is installed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Menu()
    {
        const string G = "Drone operator";
        int o = 0;
        OperatorModControls.BindingRegistry.Register(Guid, G, "Recon drone", Drone.ReconKey, "Press: launch / recall. Hold the crosshair on an enemy 1 s to mark him. W/S A/D move, scroll up/down, mouse look, V view, Backspace recall.", o++);
        OperatorModControls.BindingRegistry.Register(Guid, G, "Attack drone", Drone.AttackKey, "Press: launch. Explodes on a side/middle mouse button, when it hits someone, or on any hard hit (over ~15 km/h). Light taps just bounce. One per mission.", o++);
        OperatorModControls.SettingsRegistry.RegisterSlider(Guid, G, "Responsiveness", Drone.Response, 10f, "How quickly the drone reacts and stops. Lower = floatier, higher = snappier.", o++, "0", "%");
        OperatorModControls.SettingsRegistry.RegisterSlider(Guid, G, "Battery (minutes)", Drone.BatteryMin, 0.5f, "Flight time.", o++, "0.0", " min");
    }
}

public class DroneBehaviour : MonoBehaviour
{
    public DroneBehaviour(IntPtr ptr) : base(ptr) { }
    void LateUpdate() { try { Drone.Tick(); } catch (Exception e) { Game.Log.LogDebug("[Drone] " + e.Message); } }
    void OnGUI() { try { Drone.Draw(); } catch { } try { Marks.Draw(); } catch { } }
}
