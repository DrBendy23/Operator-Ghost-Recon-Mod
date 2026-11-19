using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace GhostReconView;

internal static class Panel
{
    internal static bool Open;
    internal static string Title = "GHOST RECON VIEW";
    internal static Func<ConfigFile> Source = () => Plugin.Config;
    internal static Func<KeyCode> OpenKey = () => KeyCode.None;

    static int _row;
    static bool _binding;
    static float _repeatAt;
    internal static readonly Dictionary<string, string> Names = new Dictionary<string, string>
    {
        { "AimDot", "Aim dot (where the gun points)" },
        { "AimDotSize", "Aim dot size" },
        { "AimZoom", "Third-person aim zoom, %" },
        { "AimDotColor", "Aim dot colour" },
        { "MapCard", "Map card" },
        { "PlaybookKeyV2", "Playbook key" },
        { "PlaybookDoubleClick", "Playbook: double-click to open" },
        { "EnemyAccuracyV3", "Enemy accuracy vs drone" },
        { "EnemySeeRange", "Enemy see range vs drone" },
        { "EnemyReaction", "Enemy reaction (0 slow, 1 normal, 2 fast)" },
        { "ClimbStep", "Drone climb per scroll" },
        { "BatteryMinutesV2", "Drone battery, minutes" },
        { "ControlMode", "Drone controls (0 custom, 1 ODG)" },
        { "BlastRadius", "Blast radius, m" },
        { "BlastDamage", "Blast damage at centre" },
        { "BlastThroughWalls", "Blast goes through walls" },
        { "BlastHitsTeammates", "Blast hits teammates" },
        { "AutoLeanReachV3", "Auto lean reach, m" },
        { "SwitchKey", "First-person aim switch" },
        { "RequireShift", "Switch needs Shift" },
        { "MicStaysOn", "Tap to keep the mic on" },
        { "MicIconV2", "MIC sign" },
        { "ShowWhoIsTalking", "Show who is talking" },
        { "DoorwayFirstPerson", "Doorways: quick first person" },
        { "DoorwayHoldSecondsV2", "Doorways: stay in first person, s" },
        { "DoorwaySwitchSecondsV2", "Doorways: switch speed, s" },
    };

    internal static string Pretty(ConfigEntryBase e)
    {
        if (e.Label != null) return e.Label;
        if (Names.TryGetValue(e.Key, out var n)) return n;
        var s = e.Key;
        int v = s.LastIndexOf('V');
        if (v > 0 && v < s.Length - 1 && char.IsDigit(s[v + 1])) s = s.Substring(0, v);
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpper(s[i]) : char.ToLower(s[i]));
        }
        return sb.ToString();
    }

    static string Show(ConfigEntryBase e)
    {
        var shown = e.Display;
        if (shown != null) return shown;
        var v = e.BoxedValue;
        if (v is bool b) return b ? "on" : "off";
        if (v is float f) return f.ToString(Mathf.Abs(f) < 10f && f != Mathf.Round(f) ? "0.00" : "0.#");
        return v?.ToString() ?? "";
    }

    internal static float Step(ConfigEntryBase e)
    {
        if (e.Range == null) return 1f;
        float span = e.Range.Hi - e.Range.Lo;
        if (span <= 2f) return 0.05f;
        if (span <= 10f) return 0.1f;
        if (span <= 60f) return 1f;
        return 5f;
    }

    static void Change(ConfigEntryBase e, int dir, bool big)
    {
        var v = e.BoxedValue;
        if (v is bool b) { e.BoxedValue = !b; return; }
        if (v is int i)
        {
            int n = i + dir * (big ? 5 : 1);
            if (e.Range != null) n = Mathf.Clamp(n, Mathf.RoundToInt(e.Range.Lo), Mathf.RoundToInt(e.Range.Hi));
            e.BoxedValue = n; return;
        }
        if (v is float f)
        {
            float st = Step(e);
            float n = Mathf.Round((f + dir * st * (big ? 5f : 1f)) / st) * st;
            if (e.Range != null) n = Mathf.Clamp(n, e.Range.Lo, e.Range.Hi);
            e.BoxedValue = n;
        }
    }

    internal static void Tick()
    {
        var cfg = Source();
        if (cfg == null) return;
        var list = cfg.Entries;
        if (_binding)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { _binding = false; return; }
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (k == KeyCode.None || k == KeyCode.Return || !Input.GetKeyDown(k)) continue;
                list[_row].BoxedValue = k;
                _binding = false;
                return;
            }
            return;
        }
        Open = false;
        if (!Open || list.Count == 0) return;
        if (Input.GetKeyDown(KeyCode.UpArrow)) _row = (_row - 1 + list.Count) % list.Count;
        if (Input.GetKeyDown(KeyCode.DownArrow)) _row = (_row + 1) % list.Count;
        _row = Mathf.Clamp(_row, 0, list.Count - 1);
        var e = list[_row];
        bool big = Plugin.ShiftHeld();
        int dir = 0;
        if (Input.GetKeyDown(KeyCode.LeftArrow)) { dir = -1; _repeatAt = Time.unscaledTime + 0.4f; }
        else if (Input.GetKeyDown(KeyCode.RightArrow)) { dir = 1; _repeatAt = Time.unscaledTime + 0.4f; }
        else if (Time.unscaledTime >= _repeatAt && !(e.BoxedValue is bool))
        {
            if (Input.GetKey(KeyCode.LeftArrow)) { dir = -1; _repeatAt = Time.unscaledTime + 0.06f; }
            else if (Input.GetKey(KeyCode.RightArrow)) { dir = 1; _repeatAt = Time.unscaledTime + 0.06f; }
        }
        if (e.BoxedValue is KeyCode)
        {
            if (Input.GetKeyDown(KeyCode.Return) || dir != 0) _binding = true;
            return;
        }
        if (Input.GetKeyDown(KeyCode.Return) && e.BoxedValue is bool) dir = 1;
        if (dir != 0) Change(e, dir, big);
    }

    internal static void Draw()
    {
        if (!Open || Event.current == null || Event.current.type != EventType.Repaint) return;
        var cfg = Source();
        if (cfg == null) return;
        var list = cfg.Entries;
        const float w = 820f, line = 22f;
        const int rows = 20;
        int first = Mathf.Clamp(_row - rows / 2, 0, Mathf.Max(0, list.Count - rows));
        int last = Mathf.Min(list.Count, first + rows);
        float h = line * (last - first) + 96f;
        float x = 40f, y = Mathf.Max(40f, (Screen.height - h) / 2f);

        GUI.color = new Color(0f, 0f, 0f, 0.82f);
        Ui.Tex(new Rect(x, y, w, h), Texture2D.whiteTexture);
        GUI.color = Color.white;
        Game.Label(new Rect(x + 14f, y + 8f, w, line), Title + "    " + (_row + 1) + " / " + list.Count);
        GUI.color = new Color(1f, 1f, 1f, 0.6f);
        Game.Label(new Rect(x + 14f, y + 28f, w, line), "Up/Down pick   Left/Right change   Shift faster   " + OpenKey() + " close");

        float ry = y + 54f;
        for (int i = first; i < last; i++, ry += line)
        {
            var e = list[i];
            bool sel = i == _row;
            if (sel)
            {
                GUI.color = new Color(1f, 0.8f, 0.2f, 0.22f);
                Ui.Tex(new Rect(x + 6f, ry, w - 12f, line), Texture2D.whiteTexture);
            }
            GUI.color = sel ? Color.yellow : Color.white;
            Game.Label(new Rect(x + 14f, ry, w - 190f, line), e.Section + " - " + Pretty(e));
            Game.Label(new Rect(x + w - 180f, ry, 170f, line), sel && _binding ? "press a key" : Show(e));
        }
        GUI.color = new Color(1f, 1f, 1f, 0.6f);
        if (_row < list.Count)
        {
            string help = list[_row].Help ?? "";
            if (help.Length > 66) help = help.Substring(0, 63) + "...";
            Game.Label(new Rect(x + 14f, y + h - 34f, w - 28f, 30f), help);
        }
        GUI.color = Color.white;
    }
}
