using System;
using System.Collections.Generic;
using UnityEngine;

namespace GhostReconView;

internal static class Marks
{
    internal static readonly Dictionary<IntPtr, float> MarkedUntil = new();
    static Health[] _all = Array.Empty<Health>(); static float _nextScan; static string _scene = "";
    static Texture2D _px;

    static void Scan()
    {
        try { var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; if (sc != _scene) { _scene = sc; MarkedUntil.Clear(); _all = Array.Empty<Health>(); } } catch { }
        if (Time.unscaledTime < _nextScan) return; _nextScan = Time.unscaledTime + 1f;
        try { _all = UnityEngine.Object.FindObjectsOfType<Health>(); } catch { }
    }

    static int LocalTeam() { try { var t = PlayerMaster.MyPlayerMaster?.MyTeamIdentifier; return t != null ? t.TeamID : int.MinValue; } catch { return int.MinValue; } }

    internal static IEnumerable<Health> LiveEnemies()
    {
        Scan();
        int team = LocalTeam();
        foreach (var h in _all)
        {
            bool ok = false;
            try
            {
                if (h != null && !h.isDead && h.isBot)
                {
                    int t = int.MinValue; try { var r = h.MyTeamIdentifierReference; if (r != null) t = r.TeamId; } catch { }
                    ok = team == int.MinValue || t != team;
                }
            }
            catch { }
            if (ok) yield return h;
        }
    }

    internal static void Draw()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (MarkedUntil.Count == 0 || Game.Pn() == null || Cursor.lockState != CursorLockMode.Locked) return;
        var cam = Game.ViewCamera; if (cam == null) return;
        if (_px == null) { _px = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave }; _px.SetPixel(0, 0, Color.white); _px.Apply(); }
        var dead = new List<IntPtr>();
        foreach (var h in _all)
        {
            try
            {
                if (h == null || !MarkedUntil.TryGetValue(h.Pointer, out var until)) continue;
                if (h.isDead || Time.unscaledTime > until) { dead.Add(h.Pointer); continue; }
                Vector3 head; try { head = h.Head != null ? h.Head.position : h.transform.position + Vector3.up * 1.7f; } catch { head = h.transform.position + Vector3.up * 1.7f; }
                var v = cam.WorldToScreenPoint(head + Vector3.up * 0.45f);
                if (v.z <= 0f) continue;
                float x = v.x, y = Screen.height - v.y;
                GUI.color = new Color(1f, 1f, 1f, 0.9f);
                for (int i = 0; i < 7; i++) GUI.DrawTexture(new Rect(x - (7 - i), y - 9 + i * 1.3f, (7 - i) * 2f, 1.4f), _px);
            }
            catch { }
        }
        foreach (var k in dead) MarkedUntil.Remove(k);
        GUI.color = Color.white;
    }
}
