using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;

[assembly: MelonInfo(typeof(GhostReconView.Entry), "Ghost Recon View", GhostReconView.Entry.Version, "Dr. Bendy")]
[assembly: MelonGame(null, null)]

namespace GhostReconView;

internal static class Plugin
{
    internal static ManualLogSource Out;
    internal static ConfigFile Config;
    internal static ConfigEntry<KeyCode> SwitchKey;
    internal static ConfigEntry<bool> RequireShift;

    internal static bool ThirdPerson => Game.ThirdPerson;
    internal static Camera TpCamera => Game.TpCamera;
    internal static bool HasPlayer => Game.Pn() != null;
    internal static bool Ads { get { try { var pn = Game.Pn(); return pn != null && pn.isADS; } catch { return false; } } }
    internal static bool ShiftHeld() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
}

public class Entry : MelonMod
{
    public const string Version = "3.1.0";
    static UnityAction _beforeRender;
    static MelonPreferences_Entry<bool> _adsToFp;
    static bool _adsLooked;
    float _noticeUntil, _nextLook;
    string _notice = "";

    public override void OnInitializeMelon()
    {
        Plugin.Out = new ManualLogSource(LoggerInstance);
        Game.Log = Plugin.Out;
        var cfg = Plugin.Config = new ConfigFile("GhostReconView");
        Plugin.SwitchKey = cfg.Bind("Keys", "SwitchKey", KeyCode.V, "First-person aim switch key.");
        Plugin.RequireShift = cfg.Bind("Keys", "RequireShift", true, "Switch needs Shift held.");

        var harmony = HarmonyInstance;
        try { Track.Init(harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Track] " + e.Message); }
        try { LaserBeams.Init(cfg); } catch (Exception e) { Plugin.Out.LogWarning("[Laser] " + e.Message); }
        try { AutoLean.Init(cfg); AutoLean.InitPatches(harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Lean] " + e.Message); }
        try { Drone.Init(cfg); Drone.InitPatches(harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Drone] " + e.Message); }
        try { SmartReload.Init(cfg, harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Reload] " + e.Message); }
        try { CinematicLook.Plugin.Init(cfg, Plugin.Out); } catch (Exception e) { Plugin.Out.LogWarning("[Cinematic] " + e.Message); }
        try { Dot.Init(cfg); } catch (Exception e) { Plugin.Out.LogWarning("[Dot] " + e.Message); }
        try { GuiText.Init(harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Text] " + e.Message); }
        try { Mic.Init(cfg, harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Mic] " + e.Message); }
        try { Doorway.Init(cfg, harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Door] " + e.Message); }
        try { DrillHouse.Install(); Map.Init(cfg, harmony); Playbook.Init(cfg); } catch (Exception e) { Plugin.Out.LogWarning("[Map] " + e.Message); }
        try { Extras.Init(cfg, harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Extras] " + e.Message); }
        try { AimZoom.Init(cfg, harmony); } catch (Exception e) { Plugin.Out.LogWarning("[Zoom] " + e.Message); }
        try { PatchThirdPersonKey(harmony); } catch (Exception e) { Plugin.Out.LogWarning("[View] " + e.Message); }

        _beforeRender = DelegateSupport.ConvertDelegate<UnityAction>(new Action(OnBeforeRender));
        Application.add_onBeforeRender(_beforeRender);
        cfg.SaveIfDirty();
        Plugin.Out.LogInfo("Ghost Recon View loaded (" + cfg.Entries.Count + " settings).");
    }

    static void PatchThirdPersonKey(HarmonyLib.Harmony harmony)
    {
        var t = AccessTools.TypeByName("ThirdPerson.ThirdPersonMod");
        var m = t == null ? null : AccessTools.Method(t, "ToggleWant");
        if (m == null) { Plugin.Out.LogWarning("[View] third person mod not found: Shift + key will also switch the view."); return; }
        harmony.Patch(m, prefix: new HarmonyMethod(typeof(Entry), nameof(ToggleWantPrefix)));
        var drive = AccessTools.Method(t, "Drive");
        if (drive != null) harmony.Patch(drive, prefix: new HarmonyMethod(typeof(Entry), nameof(DrivePrefix)));
        else Plugin.Out.LogWarning("[View] third person camera hook not found: the drone view will not work in third person.");
    }

    public static bool ToggleWantPrefix() => !Drone.Flying && !(Plugin.RequireShift.Value && Plugin.ShiftHeld() && Input.GetKey(Plugin.SwitchKey.Value));

    public static bool DrivePrefix() => !Drone.Flying;

    static MelonPreferences_Entry<bool> AdsToFp()
    {
        if (_adsLooked) return _adsToFp;
        _adsLooked = true;
        try { _adsToFp = MelonPreferences.GetEntry<bool>("ThirdPerson", "AdsToFirstPerson"); } catch { }
        return _adsToFp;
    }

    public override void OnLateUpdate()
    {
        bool locked = Cursor.lockState == CursorLockMode.Locked;
        try { Panel.Tick(); } catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        try { ModsMenu.Tick(); } catch (Exception e) { Plugin.Out.LogDebug("[Menu] " + e.Message); }
        try { AimSwitch(locked); } catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        try { MenuScreen.Tick(); } catch { }
        Timed("Track", Track.Tick);
        Timed("Reload", SmartReload.Tick);
        Timed("Lean", AutoLean.Tick);
        Timed("Extras", Extras.Tick);
        Timed("Mic", Mic.Tick);
        Timed("Drone", Drone.Tick);
        Timed("Map", Map.Tick);
        Timed("Playbook", Playbook.Tick);
        if (Time.unscaledTime >= _nextLook)
        {
            _nextLook = Time.unscaledTime + 0.25f;
            try { CinematicLook.Look.Apply(); } catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        }
        Plugin.Config.SaveIfDirty();
    }

    void AimSwitch(bool locked)
    {
        if (!locked || Panel.Open) return;
        if (!Input.GetKeyDown(Plugin.SwitchKey.Value) || (Plugin.RequireShift.Value && !Plugin.ShiftHeld())) return;
        var e = AdsToFp();
        if (e == null) return;
        e.Value = !e.Value;
        _notice = "FIRST-PERSON AIM " + (e.Value ? "ON" : "OFF");
        _noticeUntil = Time.unscaledTime + 1.5f;
    }

    static readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
    static readonly System.Collections.Generic.Dictionary<string, float> _slowAt = new System.Collections.Generic.Dictionary<string, float>();

    static void Timed(string name, Action work)
    {
        _watch.Restart();
        try { work(); } catch (Exception e) { Plugin.Out.LogDebug("[" + name + "] " + e.Message); }
        double ms = _watch.Elapsed.TotalMilliseconds;
        if (ms < 12.0) return;
        _slowAt.TryGetValue(name, out float last);
        if (Time.unscaledTime - last < 2f) return;
        _slowAt[name] = Time.unscaledTime;
        Plugin.Out.LogInfo("[Perf] " + name + " took " + ms.ToString("0") + " ms in one frame");
    }

    static void OnBeforeRender()
    {
        try { Drone.BeforeRender(); } catch { }
        Timed("Laser", LaserBeams.Tick);
    }

    public override void OnGUI()
    {
        try { Font5.FixSkin(); } catch { }
        try { Mic.Draw(); } catch { }
        try { Drone.Draw(); } catch { }
        try { Extras.Draw(); } catch { }
        try { Marks.Draw(); } catch { }
        try { Map.Draw(); } catch { }
        try { Playbook.Draw(); } catch { }
        try { Dot.Draw(); } catch { }
        try { Panel.Draw(); } catch { }
        try { Ui.End(); } catch { }
        if (Time.unscaledTime >= _noticeUntil || Event.current == null || Event.current.type != EventType.Repaint) return;
        var r = new Rect(Screen.width / 2f - 150f, Screen.height * 0.78f, 300f, 30f);
        GUI.color = Color.black; Game.Label(new Rect(r.x + 1, r.y + 1, r.width, r.height), _notice);
        GUI.color = Color.yellow; Game.Label(r, _notice);
        GUI.color = Color.white;
    }
}

internal static partial class Extras
{
    static partial void OnInit(ConfigFile cfg, HarmonyLib.Harmony h);
    static partial void OnTick();
    static partial void OnDraw();
    internal static void Init(ConfigFile cfg, HarmonyLib.Harmony h) => OnInit(cfg, h);
    internal static void Tick() => OnTick();
    internal static void Draw() => OnDraw();
}
