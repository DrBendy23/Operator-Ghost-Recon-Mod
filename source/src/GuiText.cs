using System;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

internal static class GuiText
{
    internal static void Init(HarmonyLib.Harmony h)
    {
        bool mac = false;
        try { mac = System.IO.Directory.Exists("Z:/System/Library"); } catch { }
        if (!mac) return;
        int done = 0;
        done += Hook(h, "Label", nameof(LabelPost));
        done += Hook(h, "Button", nameof(ButtonPost));
        done += Hook(h, "Box", nameof(BoxPost));
        try
        {
            var m = AccessTools.Method(typeof(GUI), "Label", new[] { typeof(Rect), typeof(string), typeof(GUIStyle) });
            if (m != null) { h.Patch(m, postfix: new HarmonyMethod(typeof(GuiText), nameof(StyledPost))); done++; }
        }
        catch (Exception e) { Plugin.Out.LogWarning("[Text] GUI.Label with style: " + e.Message); }
        Plugin.Out.LogInfo("[Text] menu text for other mods: " + done + " of 4 hooks");
    }

    static int Hook(HarmonyLib.Harmony h, string name, string post)
    {
        try
        {
            var m = AccessTools.Method(typeof(GUI), name, new[] { typeof(Rect), typeof(string) });
            if (m == null) { Plugin.Out.LogWarning("[Text] GUI." + name + " not found"); return 0; }
            h.Patch(m, postfix: new HarmonyMethod(typeof(GuiText), post));
            return 1;
        }
        catch (Exception e) { Plugin.Out.LogWarning("[Text] GUI." + name + ": " + e.Message); return 0; }
    }

    public static void LabelPost(Rect __0, string __1) { Draw(__0, __1, 0); }
    public static void ButtonPost(Rect __0, string __1) { Draw(__0, __1, 1); }
    public static void BoxPost(Rect __0, string __1) { Draw(__0, __1, 2); }

    public static void StyledPost(Rect __0, string __1, GUIStyle __2) { Styled(__0, __1, __2); }

    static int _frame = -1;
    static readonly System.Collections.Generic.HashSet<string> _seen = new System.Collections.Generic.HashSet<string>();

    static bool Fresh(Rect r, string text)
    {
        if (Time.frameCount != _frame) { _frame = Time.frameCount; _seen.Clear(); }
        return _seen.Add(((int)r.x) + "," + ((int)r.y) + "," + text);
    }

    static void Styled(Rect r, string text, GUIStyle st)
    {
        try
        {
            if (string.IsNullOrEmpty(text) || Event.current == null || Event.current.type != EventType.Repaint || !Fresh(r, text)) return;
            int size = st != null && st.fontSize > 0 ? st.fontSize : 14;
            var col = GUI.color;
            try { var tc = st.normal.textColor; col = new Color(col.r * tc.r, col.g * tc.g, col.b * tc.b, col.a * tc.a); } catch { }
            var a = st != null ? st.alignment : TextAnchor.UpperLeft;
            var lines = text.Split('\n');
            float lineH = size * 1.2f, total = lineH * lines.Length;
            int row = (int)a / 3, colm = (int)a % 3;
            float y = row == 0 ? r.y : row == 1 ? r.y + (r.height - total) * 0.5f : r.yMax - total;
            var old = GUI.color;
            GUI.color = col;
            float scale = Mathf.Max(1f, size * 0.62f / 7f);
            foreach (var l in lines)
            {
                float w = Font5.PixelWidth(l, scale);
                float x = colm == 0 ? r.x : colm == 1 ? r.x + (r.width - w) * 0.5f : r.xMax - w;
                Font5.Pixel(x, y + (lineH - 7f * scale) * 0.5f, l, scale);
                y += lineH;
            }
            GUI.color = old;
        }
        catch { }
    }

    static void Draw(Rect r, string text, int kind)
    {
        try
        {
            if (string.IsNullOrEmpty(text) || Event.current == null || Event.current.type != EventType.Repaint || !Fresh(r, text)) return;
            var lines = text.Split('\n');
            int longest = 1;
            foreach (var l in lines) if (l.Length > longest) longest = l.Length;
            float scale = Mathf.Clamp((r.width - 10f) / (longest * 6f), 1f, 2f);
            float lineH = 7f * scale + 4f;
            float total = lineH * lines.Length - 4f;
            float y = kind == 1 ? r.y + (r.height - total) / 2f : r.y + 5f;
            var old = GUI.color;
            for (int i = 0; i < lines.Length; i++, y += lineH)
            {
                float w = Font5.Width(lines[i], scale);
                float x = kind == 0 ? r.x + 5f : r.x + (r.width - w) / 2f;
                GUI.color = new Color(0f, 0f, 0f, 0.85f);
                Font5.Draw(x + 1f, y + 1f, lines[i], scale);
                GUI.color = Color.white;
                Font5.Draw(x, y, lines[i], scale);
            }
            GUI.color = old;
        }
        catch { }
    }
}
