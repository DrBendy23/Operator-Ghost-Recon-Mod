using System;
using System.Collections.Generic;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GhostReconView;

internal static class Ui
{
    sealed class Slot
    {
        internal GameObject Go;
        internal RectTransform Rt;
        internal RawImage Img;
        internal TextMeshProUGUI Txt;
        internal bool Text;
    }

    static GameObject _root;
    static RectTransform _rootRt;
    static TMP_FontAsset _font;
    static Material _mat;
    static readonly List<Slot> _slots = new List<Slot>();
    static int _used, _frame = -1;
    static bool _failed, _said;
    static float _lookAt;
    static TextMeshProUGUI _measure;

    internal static bool Ready => !_failed && _font != null && _root != null;

    static int _tries;

    static int Score(string n)
    {
        n = (n ?? "").ToLowerInvariant();
        return n.Contains("rajdhani") ? (n.Contains("semibold") ? 5 : 4) : n.Contains("opensans") || n.Contains("open sans") ? 3 : n.Contains("uni") ? 2 : 1;
    }

    static bool Find()
    {
        if (_font != null) return true;
        if (Time.unscaledTime < _lookAt) return false;
        _lookAt = Time.unscaledTime + 2f;
        _tries++;
        TMP_FontAsset best = null; int bs = -1; string how = "";
        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>(true))
            {
                TMP_FontAsset f = null; try { f = t != null ? t.font : null; } catch { }
                if (f == null) continue;
                int sc = Score(f.name); if (sc > bs) { bs = sc; best = f; how = "a game text on screen"; }
            }
        }
        catch (Exception e) { if (_tries < 3) Plugin.Out.LogInfo("[Ui] screen texts: " + e.Message); }
        if (best == null)
            try
            {
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<TMP_FontAsset>()))
                {
                    var f = o != null ? o.TryCast<TMP_FontAsset>() : null;
                    if (f == null) continue;
                    int sc = Score(f.name); if (sc > bs) { bs = sc; best = f; how = "the loaded fonts"; }
                }
            }
            catch (Exception e) { if (_tries < 3) Plugin.Out.LogInfo("[Ui] loaded fonts: " + e.Message); }
        if (best == null) try { best = TMP_Settings.defaultFontAsset; how = "the default"; } catch { }
        if (best == null) { if (_tries == 1 || _tries % 30 == 0) Plugin.Out.LogInfo("[Ui] no game font found yet (try " + _tries + ")"); return false; }
        _font = best;
        try { _mat = best.material; } catch { }
        Plugin.Out.LogInfo("[Ui] text now uses the game's font '" + best.name + "' (from " + how + ")");
        return true;
    }

    static bool Root()
    {
        if (_root != null) return true;
        try
        {
            _root = new GameObject("GRV_Text");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            var c = _root.AddComponent<Canvas>();
            c.renderMode = RenderMode.ScreenSpaceOverlay;
            c.sortingOrder = 31000;
            c.pixelPerfect = false;
            _rootRt = _root.GetComponent<RectTransform>();
            return true;
        }
        catch (Exception e) { _failed = true; Plugin.Out.LogInfo("[Ui] canvas: " + e.Message); return false; }
    }

    static Slot Next(bool text)
    {
        Slot s;
        if (_used < _slots.Count) s = _slots[_used];
        else
        {
            s = new Slot();
            s.Go = new GameObject("GRV_UiSlot");
            s.Go.transform.SetParent(_root.transform, false);
            s.Rt = s.Go.AddComponent<RectTransform>();
            s.Rt.anchorMin = new Vector2(0f, 1f); s.Rt.anchorMax = new Vector2(0f, 1f); s.Rt.pivot = new Vector2(0f, 1f);
            _slots.Add(s);
        }
        _used++;
        if (text && s.Txt == null)
        {
            var go = new GameObject("t"); go.transform.SetParent(s.Go.transform, false);
            var rt = go.AddComponent<RectTransform>(); rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            s.Txt = go.AddComponent<TextMeshProUGUI>();
            s.Txt.font = _font;
            if (_mat != null) s.Txt.fontSharedMaterial = _mat;
            s.Txt.enableWordWrapping = false;
            s.Txt.overflowMode = TextOverflowModes.Overflow;
            s.Txt.alignment = TextAlignmentOptions.TopLeft;
            s.Txt.raycastTarget = false;
            s.Txt.richText = false;
            s.Txt.margin = Vector4.zero;
        }
        else if (!text && s.Img == null)
        {
            var go = new GameObject("i"); go.transform.SetParent(s.Go.transform, false);
            var rt = go.AddComponent<RectTransform>(); rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            s.Img = go.AddComponent<RawImage>();
            s.Img.raycastTarget = false;
        }
        if (s.Txt != null && s.Txt.gameObject.activeSelf != text) s.Txt.gameObject.SetActive(text);
        if (s.Img != null && s.Img.gameObject.activeSelf != !text) s.Img.gameObject.SetActive(!text);
        s.Text = text;
        if (!s.Go.activeSelf) s.Go.SetActive(true);
        s.Go.transform.SetSiblingIndex(_used - 1);
        return s;
    }

    static bool Repaint()
    {
        var e = Event.current;
        if (e == null || e.type != EventType.Repaint) return false;
        if (_failed || !Find() || !Root()) return false;
        if (Time.frameCount != _frame) { _frame = Time.frameCount; _used = 0; }
        return true;
    }

    internal static void End()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint || _root == null) return;
        if (Time.frameCount != _frame) _used = 0;
        for (int i = _used; i < _slots.Count; i++) if (_slots[i].Go.activeSelf) _slots[i].Go.SetActive(false);
    }

    internal static void Hide()
    {
        for (int i = 0; i < _slots.Count; i++) if (_slots[i].Go.activeSelf) _slots[i].Go.SetActive(false);
    }

    static int _faults;

    static void Fault(string what, Exception e)
    {
        _faults++;
        if (_faults <= 5) Plugin.Out.LogInfo("[Ui] " + what + ": " + e.Message + " (slots rebuilt)");
        foreach (var sl in _slots) { try { if (sl.Go != null) UnityEngine.Object.Destroy(sl.Go); } catch { } }
        _slots.Clear(); _used = 0; _font = null; _mat = null; _measure = null; _lookAt = 0f;
        if (_faults > 50) { _failed = true; Plugin.Out.LogInfo("[Ui] too many errors, simple drawing used"); }
    }

    internal static void Tex(Rect r, Texture t)
    {
        if (!Repaint()) { if (_failed || _font == null) GUI.DrawTexture(r, t); return; }
        try
        {
            var s = Next(false);
            s.Img.texture = t;
            s.Img.uvRect = new Rect(0f, 0f, 1f, 1f);
            s.Img.color = GUI.color;
            s.Rt.pivot = new Vector2(0f, 1f);
            s.Rt.localEulerAngles = Vector3.zero;
            s.Rt.anchoredPosition = new Vector2(r.x, -r.y);
            s.Rt.sizeDelta = new Vector2(r.width, r.height);
        }
        catch (Exception e) { Fault("shapes", e); GUI.DrawTexture(r, t); }
    }

    internal static void TexUv(Rect r, Texture t, Rect uv)
    {
        if (!Repaint()) { if (_failed || _font == null) GUI.DrawTextureWithTexCoords(r, t, uv); return; }
        try
        {
            var s = Next(false);
            s.Img.texture = t;
            s.Img.uvRect = uv;
            s.Img.color = GUI.color;
            s.Rt.pivot = new Vector2(0f, 1f);
            s.Rt.localEulerAngles = Vector3.zero;
            s.Rt.anchoredPosition = new Vector2(r.x, -r.y);
            s.Rt.sizeDelta = new Vector2(r.width, r.height);
        }
        catch (Exception e) { Fault("shapes", e); }
    }

    internal static void TexTurned(Rect r, Texture t, float degrees)
    {
        if (!Repaint())
        {
            if (!_failed && _font != null) return;
            var m = GUI.matrix; GUIUtility.RotateAroundPivot(-degrees, r.center); GUI.DrawTexture(r, t); GUI.matrix = m;
            return;
        }
        try
        {
            var s = Next(false);
            s.Img.texture = t;
            s.Img.uvRect = new Rect(0f, 0f, 1f, 1f);
            s.Img.color = GUI.color;
            s.Rt.pivot = new Vector2(0.5f, 0.5f);
            s.Rt.localEulerAngles = new Vector3(0f, 0f, degrees);
            s.Rt.anchoredPosition = new Vector2(r.center.x, -r.center.y);
            s.Rt.sizeDelta = new Vector2(r.width, r.height);
        }
        catch (Exception e) { Fault("shapes", e); }
    }

    internal static bool Text(float x, float y, string text, float size)
    {
        if (!Repaint()) return Event.current != null && Event.current.type != EventType.Repaint && Ready;
        try
        {
            var s = Next(true);
            s.Txt.fontSize = size;
            s.Txt.color = GUI.color;
            if (s.Txt.text != text) s.Txt.text = text;
            s.Rt.pivot = new Vector2(0f, 1f);
            s.Rt.localEulerAngles = Vector3.zero;
            s.Rt.anchoredPosition = new Vector2(x, -y);
            s.Rt.sizeDelta = new Vector2(2000f, size * 1.5f);
            if (!_said) { _said = true; Plugin.Out.LogInfo("[Ui] first text drawn with '" + _font.name + "' at size " + size.ToString("0")); }
            return true;
        }
        catch (Exception e) { Fault("text", e); return false; }
    }

    internal static float Width(string text, float size)
    {
        if (!Ready) return -1f;
        try
        {
            if (_measure == null)
            {
                var go = new GameObject("GRV_UiMeasure");
                go.transform.SetParent(_root.transform, false);
                _measure = go.AddComponent<TextMeshProUGUI>();
                _measure.font = _font; _measure.enableWordWrapping = false;
                go.SetActive(false);
            }
            _measure.fontSize = size;
            return _measure.GetPreferredValues(text).x;
        }
        catch { return -1f; }
    }
}
