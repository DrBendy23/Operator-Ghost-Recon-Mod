using System;
using System.Collections.Generic;
using UnityEngine;

namespace GhostReconView;

internal static class Marks
{
    internal static readonly Dictionary<IntPtr, float> MarkedUntil = new();
    static List<Health> _all => Track.Healths; static string _scene = "";
    static Texture2D _px;
    static float _nextScene;

    static void Scan()
    {
        if (Time.unscaledTime < _nextScene) return; _nextScene = Time.unscaledTime + 2f;
        try { var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; if (sc != _scene) { _scene = sc; MarkedUntil.Clear(); } } catch { }
    }

    static int LocalTeam() { try { var t = PlayerMaster.MyPlayerMaster?.MyTeamIdentifier; return t != null ? t.TeamID : int.MinValue; } catch { return int.MinValue; } }

    internal static IEnumerable<Health> LiveEnemies()
    {
        Scan();
        int team = LocalTeam();
        for (int hi = 0; hi < _all.Count; hi++)
        {
            var h = _all[hi];
            bool ok = false;
            try
            {
                if (h != null && h.isActiveAndEnabled && !h.isDead && h.isBot)
                {
                    int t = int.MinValue; try { var r = h.MyTeamIdentifierReference; if (r != null) t = r.TeamId; } catch { }
                    ok = team == int.MinValue || t != team;
                }
            }
            catch { }
            if (ok) yield return h;
        }
    }

    internal static IEnumerable<Health> Marked()
    {
        foreach (var h in LiveEnemies())
            if (MarkedUntil.TryGetValue(h.Pointer, out var until) && Time.unscaledTime <= until) yield return h;
    }

    internal static void Draw()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (MarkedUntil.Count == 0 || Game.Pn() == null || Cursor.lockState != CursorLockMode.Locked) return;
        var cam = Game.ViewCamera; if (cam == null) return;
        if (_px == null) { _px = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave }; _px.SetPixel(0, 0, Color.white); _px.Apply(); }
        var dead = new List<IntPtr>();
        for (int hi = 0; hi < _all.Count; hi++)
        {
            var h = _all[hi];
            try
            {
                if (h == null || !MarkedUntil.TryGetValue(h.Pointer, out var until)) continue;
                if (h.isDead || Time.unscaledTime > until) { dead.Add(h.Pointer); continue; }
                Vector3 head; try { head = h.Head != null ? h.Head.position : h.transform.position + Vector3.up * 1.7f; } catch { head = h.transform.position + Vector3.up * 1.7f; }
                var v = cam.WorldToScreenPoint(head + Vector3.up * 0.45f);
                if (v.z <= 0f) continue;
                float x = v.x, y = Screen.height - v.y;
                float half = Mathf.Clamp(Screen.height * 0.17f / (2f * Mathf.Max(v.z, 1f) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad)), 2.5f, 8f);
                int rows = Mathf.Max(3, Mathf.RoundToInt(half));
                float rowH = half * 1.3f / rows;
                GUI.color = new Color(1f, 1f, 1f, 0.9f);
                for (int i = 0; i < rows; i++) { float w = half * (rows - i) / rows; GUI.DrawTexture(new Rect(x - w, y - half * 1.3f + i * rowH, w * 2f, rowH + 0.4f), _px); }
            }
            catch { }
        }
        foreach (var k in dead) MarkedUntil.Remove(k);
        GUI.color = Color.white;
    }
}
