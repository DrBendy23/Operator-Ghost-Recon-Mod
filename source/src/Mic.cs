using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

internal static class Mic
{
    internal static ConfigEntry<int> Stay;
    internal static ConfigEntry<bool> Icon, Talkers;

    static bool _on, _edge, _swapped;
    static InputKey _key = InputKey.None;
    static KeyCode _code = KeyCode.None;
    static InputLayer.InputKeyBindInstance _bind;
    static InputLayer.KeyPressType _oldType;
    static InputKey _swappedKey;
    static float _nextFind, _nextScan;
    static readonly List<PlayerVoice> _voices = new List<PlayerVoice>();
    static readonly List<string> _talking = new List<string>();
    static Texture2D _dot;

    internal static void Init(ConfigFile cfg, HarmonyLib.Harmony h)
    {
        Stay = cfg.Bind("Voice", "MicStaysOn", 1, new ConfigDescription("0 off, the game decides. 1 tap the radio key (K): mic on, tap again: mic off. 2 same, with the area voice key.", new AcceptableValueRange<int>(0, 2)));
        Icon = cfg.Bind("Voice", "MicIconV2", false, "Small MIC sign on the left while your mic is held open. Off by default: the game shows its own mic icon.");
        Talkers = cfg.Bind("Voice", "ShowWhoIsTalking", true, "Names of the other players talking right now, on the left.");
        var pf = AccessTools.Method(typeof(InputLayer.InputKeyBindInstance), "ProcessForThisFrame");
        if (pf != null) h.Patch(pf, postfix: new HarmonyMethod(typeof(Mic), nameof(BindPostfix)) { priority = Priority.Last });
        else Plugin.Out.LogWarning("[Mic] key hook not found: the mic cannot be held open");
    }

    internal static bool Holds(InputKey key) => Stay != null && Stay.Value != 0 && _key != InputKey.None && key == _key;

    public static void BindPostfix(InputLayer.InputKeyBindInstance __instance)
    {
        try
        {
            if (Stay == null || Stay.Value == 0 || _key == InputKey.None || __instance.BoundKey != _key) return;
            __instance.IsActive = _on;
            __instance.WasActivatedThisFrame = _edge;
            _edge = false;
        }
        catch { }
    }

    static bool Typing()
    {
        try { if (GUIUtility.keyboardControl != 0) return true; } catch { }
        try
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go != null && (go.GetComponent<Il2CppTMPro.TMP_InputField>() != null || go.GetComponent<UnityEngine.UI.InputField>() != null)) return true;
        }
        catch { }
        return false;
    }

    static void Restore()
    {
        if (!_swapped) return;
        _swapped = false;
        try
        {
            var kb = InputLayer.Keybinds;
            if (kb == null) return;
            foreach (var kv in kb)
            {
                if (kv.Key != _swappedKey || kv.Value == null) continue;
                var b = kv.Value.KeyBind; b.PressType = _oldType; kv.Value.KeyBind = b;
            }
        }
        catch (Exception e) { Plugin.Out.LogDebug("[Mic] restore: " + e.Message); }
    }

    static void Find(InputKey want)
    {
        if (_bind != null && _key == want) return;
        if (Time.unscaledTime < _nextFind) return;
        _nextFind = Time.unscaledTime + 2f;
        try
        {
            var kb = InputLayer.Keybinds;
            if (kb == null) return;
            foreach (var kv in kb)
            {
                if (kv.Key != want || kv.Value == null) continue;
                var b = kv.Value.KeyBind;
                if (b.PrimaryKey == KeyCode.None) return;
                Restore();
                _bind = kv.Value; _key = want; _code = b.PrimaryKey;
                if (b.PressType != InputLayer.KeyPressType.Continuous)
                {
                    _oldType = b.PressType; _swappedKey = want; _swapped = true;
                    b.PressType = InputLayer.KeyPressType.Continuous; kv.Value.KeyBind = b;
                }
                Plugin.Out.LogInfo("[Mic] tap " + _code + " to open or close the mic");
                return;
            }
        }
        catch (Exception e) { Plugin.Out.LogDebug("[Mic] find: " + e.Message); }
    }

    internal static void Tick()
    {
        if (Stay == null) return;
        Scan();
        int mode = Stay.Value;
        if (mode == 0)
        {
            if (_on || _key != InputKey.None) { _on = false; _edge = false; Restore(); _key = InputKey.None; _bind = null; }
            return;
        }
        var want = mode == 2 ? InputKey.Voicechat : InputKey.RadioChat;
        if (_key != want) { _on = false; _edge = false; _bind = null; }
        Find(want);
        if (_bind != null && _code != KeyCode.None && Plugin.HasPlayer && Cursor.lockState == CursorLockMode.Locked && !Panel.Open && Input.GetKeyDown(_code) && !Typing())
        {
            _on = !_on; _edge = _on;
            Plugin.Out.LogInfo("[Mic] " + (_on ? "OPEN" : "CLOSED"));
        }
        if (_on && !Plugin.HasPlayer) { _on = false; _edge = false; }
    }

    static void Scan()
    {
        if (Talkers == null || !Talkers.Value) { _talking.Clear(); return; }
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 2f;
            _voices.Clear();
            try
            {
                foreach (var pm in Map.Masters())
                {
                    if (pm == null) continue;
                    PlayerVoice v = null;
                    try { v = pm.GetComponentInChildren<PlayerVoice>(true); } catch { }
                    if (v == null) try { var pn = pm.PlayerSpawnedObject; if (pn != null) v = pn.GetComponentInChildren<PlayerVoice>(true); } catch { }
                    if (v != null) _voices.Add(v);
                }
            }
            catch { }
        }
        _talking.Clear();
        foreach (var v in _voices)
        {
            try
            {
                if (v == null) continue;
                var vc = v.VoiceChatComponent;
                if (vc == null || vc.isLocalPlayer || !vc.isSpeaking.Value) continue;
                string name = v.MyPlayerMaster != null ? v.MyPlayerMaster.thisPlayerName : null;
                if (string.IsNullOrEmpty(name)) name = "PLAYER";
                _talking.Add(name + (v.IsRadioChat ? "  RADIO" : ""));
            }
            catch { }
        }
    }

    internal static void Draw()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        bool icon = _on && Icon != null && Icon.Value;
        if (!icon && _talking.Count == 0) return;
        if (_dot == null) { _dot = new Texture2D(1, 1); _dot.SetPixel(0, 0, Color.white); _dot.Apply(); _dot.hideFlags = HideFlags.HideAndDontSave; }
        float x = 26f, y = Screen.height * 0.36f - 54f;
        var old = GUI.color;
        if (icon) { Row(x, y, "MIC", 62f); y += 24f; }
        foreach (var t in _talking) { Row(x, y, t, 30f + Font5.Width(t, 1.5f)); y += 24f; }
        GUI.color = old;
    }

    static void Row(float x, float y, string text, float w)
    {
        GUI.color = new Color(0f, 0f, 0f, 0.55f); Ui.Tex(new Rect(x - 6f, y - 5f, w, 21f), _dot);
        GUI.color = new Color(0.25f, 0.9f, 0.4f, 1f); Ui.Tex(new Rect(x, y + 1f, 9f, 9f), _dot);
        GUI.color = Color.white; Font5.Draw(x + 15f, y, text, 1.5f);
    }
}
