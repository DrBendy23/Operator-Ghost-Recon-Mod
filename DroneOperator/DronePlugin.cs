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

[BepInPlugin(Guid, "Drone Operator", "1.3")]
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Menu()
    {
        const string G = "Drone operator";
        int o = 0;
        OperatorModControls.BindingRegistry.Register(Guid, G, "Recon drone", Drone.ReconKey, "Press: launch / recall. Hold the crosshair on an enemy 1 s to mark him. W/S A/D move, scroll up/down, mouse look, V view, Backspace recall.", o++);
        OperatorModControls.BindingRegistry.Register(Guid, G, "Attack drone", Drone.AttackKey, "Press: launch. Explodes on the detonate key, when it hits someone, or on any hard hit (over ~15 km/h). Light taps just bounce. One per mission.", o++);
        OperatorModControls.BindingRegistry.Register(Guid, G, "Detonate", Drone.DetonateKey, "Blows up the attack drone while you fly it. Any key or mouse button (mouse 3/4/5 work).", o++);
        OperatorModControls.SettingsRegistry.RegisterInteger(Guid, G, "Flip mode  (0 triple-tap, 1 flip key, 2 right click)", Drone.FlipMode, "0: triple-tap W, S, A or D to flip that way. 1: hold W, S, A or D and press the Flip key. 2: hold W, S, A or D and right-click.", o++);
        OperatorModControls.BindingRegistry.Register(Guid, G, "Flip", Drone.FlipKey, "Used when Flip mode is 1: hold W (front flip), S (back flip), A or D (roll) and press this. Any key, or mouse middle/4/5.", o++);
        OperatorModControls.BindingRegistry.Register(Guid, G, "Pick up drone", Drone.PickupKey, "A drone you land more than 3 m away, or that runs out of battery, drops where it is. From 3.5 m or lower it stays in one piece: walk up to it and press this to take it back.", o++);
        OperatorModControls.SettingsRegistry.RegisterSlider(Guid, G, "Responsiveness", Drone.Response, 10f, "How quickly the drone reacts and stops. Lower = floatier, higher = snappier.", o++, "0", "%");
        OperatorModControls.SettingsRegistry.RegisterSlider(Guid, G, "Battery (minutes)", Drone.BatteryMin, 0.5f, "Flight time.", o++, "0.0", " min");
        OperatorModControls.SettingsRegistry.RegisterSlider(Guid, G, "Climb per scroll", Drone.Climb, 0.1f, "How many meters the drone goes up or down per scroll-wheel notch.", o++, "0.0", " m");
        OperatorModControls.SettingsRegistry.RegisterSlider(Guid, G, "Enemy see range", Drone.SeeRange, 5f, "How far enemies can spot the drone at all, meters. 0 = never. Hearing is 60% of this (attack) / 40% (recon). At most 3 shooters at once.", o++, "0", " m");
        OperatorModControls.SettingsRegistry.RegisterInteger(Guid, G, "Enemy reaction  (0 slow, 1 normal, 2 fast)", Drone.Reaction, "How fast an enemy who sees the drone opens fire. 0 slow: 3 s close, 9 s at the edge. 1 normal: 2 s close, 6 s at the edge. 2 fast: 1.2 s close, 3.6 s at the edge. They only see it in front of them. A quick fly-by is gone before he reacts.", o++);
        OperatorModControls.SettingsRegistry.RegisterSlider(Guid, G, "Enemy accuracy", Drone.Accuracy, 5f, "Enemy skill against your drone (15-50). Hovering still near them they hit it; far away or moving they rarely do; flips are nearly impossible to hit.", o++, "0", "%");
    }
}

public class DroneBehaviour : MonoBehaviour
{
    public DroneBehaviour(IntPtr ptr) : base(ptr) { }
    void LateUpdate() { try { Drone.Tick(); } catch (Exception e) { Game.Log.LogDebug("[Drone] " + e.Message); } }
    void OnGUI() { try { Drone.Draw(); } catch { } try { Marks.Draw(); } catch { } }
}
