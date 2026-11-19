using System.Collections.Generic;
using UnityEngine;

namespace GhostReconView;

internal static class Font5
{
    const int W = 5, H = 7;
    static bool _built;
    static readonly Dictionary<char, Texture2D> Tex = new();

    static readonly string[] Glyphs =
    {
        "A:01110,10001,10001,11111,10001,10001,10001",
        "B:11110,10001,10001,11110,10001,10001,11110",
        "C:01110,10001,10000,10000,10000,10001,01110",
        "D:11110,10001,10001,10001,10001,10001,11110",
        "E:11111,10000,10000,11110,10000,10000,11111",
        "F:11111,10000,10000,11110,10000,10000,10000",
        "G:01110,10001,10000,10111,10001,10001,01111",
        "H:10001,10001,10001,11111,10001,10001,10001",
        "I:01110,00100,00100,00100,00100,00100,01110",
        "J:00111,00010,00010,00010,00010,10010,01100",
        "K:10001,10010,10100,11000,10100,10010,10001",
        "L:10000,10000,10000,10000,10000,10000,11111",
        "M:10001,11011,10101,10101,10001,10001,10001",
        "N:10001,11001,10101,10011,10001,10001,10001",
        "O:01110,10001,10001,10001,10001,10001,01110",
        "P:11110,10001,10001,11110,10000,10000,10000",
        "Q:01110,10001,10001,10001,10101,10010,01101",
        "R:11110,10001,10001,11110,10100,10010,10001",
        "S:01111,10000,10000,01110,00001,00001,11110",
        "T:11111,00100,00100,00100,00100,00100,00100",
        "U:10001,10001,10001,10001,10001,10001,01110",
        "V:10001,10001,10001,10001,10001,01010,00100",
        "W:10001,10001,10001,10101,10101,11011,10001",
        "X:10001,10001,01010,00100,01010,10001,10001",
        "Y:10001,10001,01010,00100,00100,00100,00100",
        "Z:11111,00001,00010,00100,01000,10000,11111",
        "0:01110,10001,10011,10101,11001,10001,01110",
        "1:00100,01100,00100,00100,00100,00100,01110",
        "2:01110,10001,00001,00010,00100,01000,11111",
        "3:11110,00001,00001,01110,00001,00001,11110",
        "4:00010,00110,01010,10010,11111,00010,00010",
        "5:11111,10000,11110,00001,00001,10001,01110",
        "6:00110,01000,10000,11110,10001,10001,01110",
        "7:11111,00001,00010,00100,01000,01000,01000",
        "8:01110,10001,10001,01110,10001,10001,01110",
        "9:01110,10001,10001,01111,00001,00010,01100",
        ".:00000,00000,00000,00000,00000,01100,01100",
        ",:00000,00000,00000,00000,01100,00100,01000",
        "::00000,01100,01100,00000,01100,01100,00000",
        "!:00100,00100,00100,00100,00100,00000,00100",
        "?:01110,10001,00001,00010,00100,00000,00100",
        "-:00000,00000,00000,11111,00000,00000,00000",
        "+:00000,00100,00100,11111,00100,00100,00000",
        "/:00001,00001,00010,00100,01000,10000,10000",
        "%:11001,11001,00010,00100,01000,10011,10011",
        "(:00010,00100,01000,01000,01000,00100,00010",
        "):01000,00100,00010,00010,00010,00100,01000",
        "=:00000,00000,11111,00000,11111,00000,00000",
        "':00100,00100,01000,00000,00000,00000,00000",
        "_:00000,00000,00000,00000,00000,00000,11111",
        "<:00010,00100,01000,10000,01000,00100,00010",
        ">:01000,00100,00010,00001,00010,00100,01000",
        "[:01110,01000,01000,01000,01000,01000,01110",
        "]:01110,00010,00010,00010,00010,00010,01110",
        "*:00000,10101,01110,11111,01110,10101,00000",
        "#:01010,01010,11111,01010,11111,01010,01010",
        "\\:10000,10000,01000,00100,00010,00001,00001",
        ";:00000,01100,01100,00000,01100,00100,01000",
        "|:00100,00100,00100,00100,00100,00100,00100",
        "\":01010,01010,00000,00000,00000,00000,00000",
        "&:01100,10010,10100,01000,10101,10010,01101",
        "@:01110,10001,10111,10101,10111,10000,01110",
        "$:00100,01111,10100,01110,00101,11110,00100",
        "^:00100,01010,10001,00000,00000,00000,00000",
        "~:00000,00000,01000,10101,00010,00000,00000",
    };

    static void Build()
    {
        _built = true;
        var clear = new Color(1f, 1f, 1f, 0f);
        for (int g = 0; g < Glyphs.Length; g++)
        {
            char c = Glyphs[g][0];
            var lines = Glyphs[g].Substring(2).Split(',');
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;
            for (int r = 0; r < H; r++)
                for (int k = 0; k < W; k++)
                    tex.SetPixel(k, H - 1 - r, lines[r][k] == '1' ? Color.white : clear);
            tex.Apply(false, false);
            Tex[c] = tex;
        }
    }

    static Font _font;
    static GUIStyle _style;
    static bool _looked, _said;
    const float Cap = 7.6f;

    static GUIStyle Style()
    {
        if (_style != null) return _style;
        if (_looked || Event.current == null) return null;
        _looked = true;
        try
        {
            Font pick = null; int best = 0;
            foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (f == null) continue;
                string n = f.name.ToLowerInvariant();
                int score = n.Contains("rajdhani") ? (n.Contains("semibold") ? 4 : 3) : n.Contains("opensans") || n.Contains("open sans") ? 2 : 0;
                if (score > best) { best = score; pick = f; }
            }
            var st = new GUIStyle(GUI.skin.label);
            if (pick != null) st.font = pick;
            if (st.font == null || !Works(st.font)) { var os = OsFont(); if (os != null) st.font = os; }
            st.wordWrap = false; st.richText = false; st.clipping = TextClipping.Overflow; st.alignment = TextAnchor.UpperLeft;
            st.padding = new RectOffset(0, 0, 0, 0); st.margin = new RectOffset(0, 0, 0, 0);
            _font = st.font;
            _style = st;
            pick = st.font;
            Plugin.Out.LogInfo("[Font] text uses the game font '" + (pick != null ? pick.name : "default") + "'");
        }
        catch (System.Exception e) { Plugin.Out.LogInfo("[Font] game font not found, using the pixel font: " + e.Message); _style = null; }
        return _style;
    }

    static bool _skinFixed;

    internal static void FixSkin()
    {
        if (_skinFixed || Event.current == null) return;
        _skinFixed = true;
        var skin = GUI.skin;
        if (skin == null) return;
        if (skin.font != null && Works(skin.font) && (skin.label.font == null || Works(skin.label.font))) { Plugin.Out.LogInfo("[Font] on-screen text font '" + skin.font.name + "' already draws"); return; }
        Font pick = null; int best = 0;
        foreach (var f in Resources.FindObjectsOfTypeAll<Font>())
        {
            if (f == null || !Works(f)) continue;
            string n = f.name.ToLowerInvariant();
            int score = n.Contains("rajdhani") ? 4 : n.Contains("opensans") || n.Contains("open sans") ? 3 : 1;
            if (score > best) { best = score; pick = f; }
        }
        if (pick == null) pick = OsFont();
        if (pick == null) { Plugin.Out.LogInfo("[Font] no font draws on-screen text; other mods' text may stay blank"); return; }
        skin.font = pick;
        if (skin.label.font != null && !Works(skin.label.font)) skin.label.font = pick;
        Plugin.Out.LogInfo("[Font] on-screen text for all mods now uses '" + pick.name + "'");
    }

    static bool Works(Font f)
    {
        try
        {
            f.RequestCharactersInTexture("AW09", 24, FontStyle.Normal);
            if (!f.HasCharacter('A')) return false;
            var m = f.material;
            return m != null && m.mainTexture != null && m.mainTexture.width > 0;
        }
        catch { return false; }
    }

    static Font OsFont()
    {
        var tries = new System.Collections.Generic.List<System.Func<Font>>
        {
            () => Font.GetDefault(),
            () => Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"),
            () => Resources.GetBuiltinResource<Font>("Arial.ttf"),
            () => Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Helvetica", "Liberation Sans", "Tahoma", "Verdana", "DejaVu Sans", "Segoe UI" }, 24)
        };
        string[] names = { "engine default", "built-in LegacyRuntime", "built-in Arial", "system font" };
        for (int k = 0; k < tries.Count; k++)
        {
            Font f = null;
            try { f = tries[k](); } catch { }
            if (f == null) { Plugin.Out.LogInfo("[Font] " + names[k] + ": not available"); continue; }
            bool ok = Works(f);
            Plugin.Out.LogInfo("[Font] " + names[k] + " '" + f.name + "': " + (ok ? "letters render" : "no letters"));
            if (ok) return f;
        }
        return null;
    }

    static int Size(float scale) => Mathf.Max(8, Mathf.RoundToInt(Cap * scale * 1.45f));
    static bool _noMeasure, _broken;

    internal static float Width(string text, float scale)
    {
        if (text == null) return 0f;
        { float uw = Ui.Width(text.ToUpperInvariant(), Size(scale)); if (uw >= 0f) return uw; }
        var st = _broken ? null : Style();
        if (st == null) return text.Length * (W + 1) * scale;
        int size = Size(scale);
        if (!_noMeasure)
        {
            try { st.fontSize = size; return st.CalcSize(new GUIContent(text.ToUpperInvariant())).x; }
            catch (System.Exception e) { _noMeasure = true; Plugin.Out.LogInfo("[Font] text width is estimated: " + e.Message); }
        }
        return text.Length * size * 0.62f;
    }

    internal static void Draw(float x, float y, string text, float scale)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Ui.Text(x, y - Size(scale) * 0.18f, text.ToUpperInvariant(), Size(scale))) return;
        var st = _broken ? null : Style();
        if (st != null)
        {
            var old = GUI.color;
            try
            {
                int size = Size(scale);
                st.fontSize = size;
                st.normal.textColor = old;
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y - size * 0.22f, 4000f, size * 1.4f), text.ToUpperInvariant(), st);
                GUI.color = old;
                if (!_said) { _said = true; Plugin.Out.LogInfo("[Font] first text drawn with '" + (st.font != null ? st.font.name : "default") + "' at size " + size); }
                return;
            }
            catch (System.Exception e)
            {
                GUI.color = old;
                _broken = true;
                Plugin.Out.LogInfo("[Font] the game font could not draw (" + e.Message + "), simple letters used instead");
            }
        }
        if (!_built) Build();
        float adv = (W + 1) * scale, gw = W * scale, gh = H * scale;
        for (int i = 0; i < text.Length; i++, x += adv)
        {
            char c = char.ToUpperInvariant(text[i]);
            if (c == '\u2014' || c == '\u2013') c = '-';
            if (c == ' ' || !Tex.TryGetValue(c, out var tex) || tex == null) continue;
            GUI.DrawTexture(new Rect(x, y, gw, gh), tex);
        }
    }
}
