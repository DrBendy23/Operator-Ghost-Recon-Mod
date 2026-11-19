using System;
using BepInEx.Configuration;
using UnityEngine;

namespace GhostReconView;

internal static class Dot
{
    internal static ConfigEntry<int> Mode;
    internal static ConfigEntry<float> Size;
    internal static ConfigEntry<int> Tint;
    internal static Texture2D Round { get { if (_tex == null) Build(); return _tex; } }

    static Texture2D _tex;
    static Vector2 _pos; static bool _has; static float _lastSeen;
    static string _state = "";

    internal static void Init(ConfigFile cfg)
    {
        Mode = cfg.Bind("Laser", "AimDot", 2, new ConfigDescription("Small dot where your gun points. 0 off. 1 only when no laser beam is showing. 2 always.", new AcceptableValueRange<int>(0, 2)));
        Tint = cfg.Bind("Laser", "AimDotColor", 0, new ConfigDescription("0 white, 1 green, 2 red, 3 yellow, 4 blue.", new AcceptableValueRange<int>(0, 4)));
        Size = cfg.Bind("Laser", "AimDotSize", 3f, new ConfigDescription("Aim dot size, pixels.", new AcceptableValueRange<float>(3f, 14f)));
    }

    static void Build()
    {
        const int n = 32;
        _tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        _tex.hideFlags = HideFlags.HideAndDontSave;
        float c = (n - 1) / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                _tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(c - d + 0.5f)));
            }
        _tex.Apply();
    }

    static void Say(string s)
    {
        if (s == _state) return;
        _state = s;
        Plugin.Out.LogInfo("[Dot] " + s);
    }

    internal static void Draw()
    {
        if (Mode == null || Mode.Value == 0 || Event.current == null || Event.current.type != EventType.Repaint) return;
        var pn = Game.Pn();
        if (pn == null || Cursor.lockState != CursorLockMode.Locked || Drone.Flying || Panel.Open) { _has = false; return; }
        if (Mode.Value == 1 && LaserBeams.Shown > 0) { _has = false; Say("hidden, the laser beam is showing"); return; }
        if (!Plugin.ThirdPerson && Plugin.Ads) { _has = false; return; }
        var m = Game.Muzzle();
        var cam = Game.ViewCamera;
        if (m == null || cam == null) { _has = false; Say("hidden, no weapon in hands"); return; }
        Vector3 aim = cam.transform.forward;
        if (!Game.Barrel(m, aim, out var dir)) { _has = false; Say("hidden, weapon lowered"); return; }
        Game.Cast(m.position, dir, 150f, pn.transform, out var p);
        bool onLaser = Time.frameCount - LaserBeams.MyEndFrame <= 1;
        if (onLaser) p = LaserBeams.MyEnd;
        var sp = cam.WorldToScreenPoint(p);
        if (sp.z <= 0.01f) { _has = false; return; }
        var target = new Vector2(sp.x, Screen.height - sp.y);
        float now = Time.unscaledTime;
        if (!_has || now - _lastSeen > 0.25f) _pos = target;
        else _pos = Vector2.Lerp(_pos, target, 1f - Mathf.Exp(-28f * Time.unscaledDeltaTime));
        _has = true; _lastSeen = now;
        if (_tex == null) Build();
        float s = Size.Value;
        var old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(_pos.x - (s + 3f) / 2f, _pos.y - (s + 3f) / 2f, s + 3f, s + 3f), _tex);
        GUI.color = Tint.Value switch { 1 => new Color(0.3f, 1f, 0.3f, 0.95f), 2 => new Color(1f, 0.25f, 0.2f, 0.95f), 3 => new Color(1f, 0.9f, 0.2f, 0.95f), 4 => new Color(0.35f, 0.65f, 1f, 0.95f), _ => new Color(1f, 1f, 1f, 0.95f) };
        GUI.DrawTexture(new Rect(_pos.x - s / 2f, _pos.y - s / 2f, s, s), _tex);
        GUI.color = old;
        Say(onLaser ? "showing on the laser dot" : "showing");
    }
}
