using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.Events;

namespace GhostReconView;

[BepInPlugin(Guid, "Ghost Recon View", "2.0.1")]
[BepInDependency("community.operator.thirdperson", BepInDependency.DependencyFlags.HardDependency)]
[BepInDependency("local.operator.modcontrols", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BasePlugin
{
    public const string Guid = "ghostreconview.operator";

    internal static ManualLogSource Out;
    internal static ConfigEntry<KeyCode> SwitchKey;
    internal static ConfigEntry<int> CrosshairColor;
    internal static ConfigEntry<bool> RequireShift;
    internal static ConfigEntry<bool> Enabled;

    static FieldInfo fThirdPerson, fAds, fTp, fTpCamera, fView;
    static MethodInfo mToggle, mReset;
    static bool _ownToggle;
    internal static float LastOwnToggle = -10f;
    static UnityAction _beforeRender;

    public override void Load()
    {
        Out = Log; Game.Log = Log;
        SwitchKey = Config.Bind("Keys", "SwitchKey", KeyCode.V, "First-person aim switch key.");
        RequireShift = Config.Bind("Keys", "RequireShift", true, "Switch needs Shift held.");
        Enabled = Config.Bind("State", "FirstPersonAim", false, "First-person aim on/off.");
        CrosshairColor = Config.Bind("Crosshair", "Color", 0, new ConfigDescription("0 white, 1 green, 2 red, 3 yellow, 4 blue.", new AcceptableValueRange<int>(0, 4)));

        var game = AccessTools.TypeByName("OperatorTPS.Game");
        if (game == null || AccessTools.Method(game, "ToggleThirdPerson") == null) { Out.LogError("BASS Third Person mod not found."); return; }
        var cam = AccessTools.TypeByName("OperatorTPS.ThirdPersonCamera");
        fThirdPerson = AccessTools.Field(game, "ThirdPerson");
        fAds = AccessTools.Field(game, "Ads");
        fTp = AccessTools.Field(game, "Tp");
        fTpCamera = AccessTools.Field(game, "TpCamera");
        fView = AccessTools.Field(game, "View");
        mToggle = AccessTools.Method(game, "ToggleThirdPerson");
        mReset = cam != null ? AccessTools.Method(cam, "Reset") : null;

        var harmony = new Harmony(Guid);
        harmony.Patch(mToggle, prefix: new HarmonyMethod(typeof(Plugin), nameof(TogglePrefix)));
        try { LaserBeams.Init(Config); } catch (Exception e) { Out.LogWarning(e.Message); }
        try { AutoLean.Init(Config); AutoLean.InitPatches(harmony); } catch (Exception e) { Out.LogWarning("[Lean] " + e.Message); }
        try { SmartReload.Init(Config, harmony); } catch (Exception e) { Out.LogWarning("[Reload] " + e.Message); }
        try { MenuBridge.Register(); } catch { }

        ClassInjector.RegisterTypeInIl2Cpp<AimBehaviour>();
        AddComponent<AimBehaviour>();
        _beforeRender = DelegateSupport.ConvertDelegate<UnityAction>(new Action(AimBehaviour.OnBeforeRender));
        Application.add_onBeforeRender(_beforeRender);
        Out.LogInfo("Ghost Recon View loaded.");
    }

    internal static bool ShiftHeld() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

    public static bool TogglePrefix(ref bool __result)
    {
        if (_ownToggle || !(RequireShift.Value && ShiftHeld() && SwitchKey.Value == BassToggleKey)) return true;
        __result = false;
        return false;
    }

    internal static bool ThirdPerson => fThirdPerson != null && (bool)fThirdPerson.GetValue(null);
    internal static bool Ads => fAds != null && (bool)fAds.GetValue(null);
    internal static bool HasPlayer => fTp != null && fTp.GetValue(null) != null;
    internal static Camera TpCamera => fTpCamera?.GetValue(null) as Camera;
    internal static Transform View => fView?.GetValue(null) as Transform;

    internal static void Toggle()
    {
        _ownToggle = true;
        try
        {
            if ((bool)mToggle.Invoke(null, null)) mReset?.Invoke(null, null);
            LastOwnToggle = Time.unscaledTime;
        }
        catch (Exception e) { Out.LogWarning(e.Message); }
        finally { _ownToggle = false; }
    }

    internal static KeyCode BassToggleKey
    {
        get
        {
            var st = AccessTools.TypeByName("OperatorTPS.Settings");
            return (st == null ? null : AccessTools.Field(st, "ToggleKey")?.GetValue(null) as ConfigEntry<KeyCode>)?.Value ?? KeyCode.V;
        }
    }

    internal static ConfigEntry<bool> BassSetting(string name)
    {
        var st = AccessTools.TypeByName("OperatorTPS.Settings");
        return st == null ? null : AccessTools.Field(st, name)?.GetValue(null) as ConfigEntry<bool>;
    }
}

public class AimBehaviour : MonoBehaviour
{
    public AimBehaviour(IntPtr ptr) : base(ptr) { }

    const float GlideIn = 0.22f, GlideOut = 0.25f;
    enum Phase { None, Entering, Exiting }

    static Phase _phase;
    static float _t;
    static float _fpFov = -1f;
    bool _wasAds, _switchedForAim;
    float _noticeUntil;
    string _notice = "";

    void LateUpdate()
    {
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        try
        {
        }
        catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        if (MenuBridge.Dirty && locked)
        {
            MenuBridge.Dirty = false;
            try { MenuBridge.Register(); } catch { }
        }
        try { Tick(locked); } catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        try { SmartReload.Tick(); } catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        try { AutoLean.Tick(); } catch (Exception e) { Plugin.Out.LogDebug("[Lean] " + e.Message); }
    }

    void StartEnter() { _phase = Phase.Entering; _t = 0f; }

    void FinishEnter()
    {
        _phase = Phase.None;
        Plugin.Toggle();
        _switchedForAim = true;
    }

    void StartExit()
    {
        Plugin.Toggle();
        _switchedForAim = false;
        _phase = Phase.Exiting; _t = 0f;
    }

    void Tick(bool locked)
    {
        if (!Plugin.HasPlayer) { _switchedForAim = _wasAds = false; _phase = Phase.None; return; }
        bool tp = Plugin.ThirdPerson, ads = Plugin.Ads;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);

        if (!tp)
        {
            var c = Plugin.View != null ? Plugin.View.GetComponent<Camera>() : null;
            if (c != null) _fpFov = c.fieldOfView;
        }

        if (_phase == Phase.Entering)
        {
            if (!ads || !tp) _phase = Phase.None;
            else if ((_t += dt / GlideIn) >= 1f) FinishEnter();
        }
        else if (_phase == Phase.Exiting && ((_t += dt / GlideOut) >= 1f || (ads && Plugin.Enabled.Value)))
            _phase = Phase.None;

        if (locked && Input.GetKeyDown(Plugin.SwitchKey.Value) && (!Plugin.RequireShift.Value || Plugin.ShiftHeld()))
        {
            Plugin.Enabled.Value = !Plugin.Enabled.Value;
            _notice = "FIRST-PERSON AIM " + (Plugin.Enabled.Value ? "ON" : "OFF");
            _noticeUntil = Time.unscaledTime + 1.5f;
            if (!Plugin.Enabled.Value)
            {
                if (_phase == Phase.Entering) _phase = Phase.None;
                if (_switchedForAim) { StartExit(); tp = true; }
            }
            else if (ads && tp && _phase == Phase.None) StartEnter();
        }

        if (_switchedForAim && tp && Time.unscaledTime - Plugin.LastOwnToggle > 0.4f) _switchedForAim = false;

        if (Plugin.Enabled.Value)
        {
            if (ads && !_wasAds && tp && _phase == Phase.None) StartEnter();
            else if (!ads && _wasAds && _switchedForAim) StartExit();
        }
        _wasAds = ads;
    }

    internal static void OnBeforeRender()
    {
        try { BlendCamera(); } catch { }
        try { LaserBeams.Tick(); } catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
    }

    static void BlendCamera()
    {
        if (_phase == Phase.None || !Plugin.ThirdPerson) return;
        var cam = Plugin.TpCamera;
        var view = Plugin.View;
        if (cam == null || view == null) return;

        float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_t));
        float k = _phase == Phase.Entering ? s : 1f - s;
        Transform ct = cam.transform;
        float eyeFov = _fpFov > 1f ? _fpFov : cam.fieldOfView;
        ct.SetPositionAndRotation(Vector3.Lerp(ct.position, view.position, k), Quaternion.Slerp(ct.rotation, view.rotation, k));
        cam.fieldOfView = Mathf.Lerp(cam.fieldOfView, eyeFov, k);
    }

    void OnGUI()
    {
        if (Time.unscaledTime >= _noticeUntil || Event.current == null || Event.current.type != EventType.Repaint) return;
        var r = new Rect(Screen.width / 2f - 150f, Screen.height * 0.78f, 300f, 30f);
        GUI.color = Color.black; GUI.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), _notice);
        GUI.color = Color.yellow; GUI.Label(r, _notice);
        GUI.color = Color.white;
    }
}
