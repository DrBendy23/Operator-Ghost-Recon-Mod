using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GhostReconView;

internal static class Map
{
    internal sealed class Door { internal Vector3 C, H; internal DoorV2 D; }

    internal struct Mate { internal Vector3 P; internal string Name; }

    sealed class Layer
    {
        internal readonly byte[] S = new byte[N * N];
        internal readonly Color32[] Px = new Color32[N * N];
        internal Texture2D Tex;
        internal int Pcx = int.MinValue, Pcz;
        internal bool Dirty;
        internal float Used;
    }

    const float Cell = 0.4f;
    const int N = 160, Reach = 44, NearReach = 14;
    const byte Unknown = 0, Out = 1, In = 2, Low = 3, Wall = 4, High = 5;
    static readonly Color32[] Colors =
    {
        new Color32(0, 0, 0, 0),
        new Color32(64, 74, 62, 120),
        new Color32(58, 72, 96, 175),
        new Color32(30, 34, 40, 90),
        new Color32(232, 236, 242, 255),
        new Color32(150, 156, 166, 200),
    };

    internal static ConfigEntry<bool> On;
    internal static Func<IEnumerable<Health>> EnemyDots = Marks.Marked;
    internal static readonly List<Door> Doors = new List<Door>();
    internal static readonly List<Mate> Mates = new List<Mate>();
    static readonly Dictionary<int, Layer> _layers = new Dictionary<int, Layer>();
    static Layer _cur;
    static int _band;
    static Vector2Int[] _order;
    static int _far, _near;
    static float _ground;
    static string _scene = "";
    static bool _read;
    static float _nextMates, _nextApply, _radarTry, _nextPms, _reread;
    static HarmonyLib.Harmony _h;
    static bool _radarHidden;
    static Texture2D _dot, _arrow;
    static Rect _card;
    static float _k = 8f, _cx, _cz;
    static readonly Stopwatch _sw = new Stopwatch();

    internal static void Init(ConfigFile cfg, HarmonyLib.Harmony h)
    {
        On = cfg.Bind("Map", "MapCard", true, "Bottom-left map card: a live scan of everything around you, centered on you, with your teammates.");
        _h = h;
        var list = new List<Vector2Int>();
        for (int z = -Reach; z <= Reach; z++)
            for (int x = -Reach; x <= Reach; x++)
                if (x * x + z * z <= Reach * Reach) list.Add(new Vector2Int(x, z));
        list.Sort((a, b) => a.sqrMagnitude.CompareTo(b.sqrMagnitude));
        _order = list.ToArray();
    }

    static void HideRadar()
    {
        if (_radarHidden || Time.unscaledTime < _radarTry) return;
        _radarTry = Time.unscaledTime + 5f;
        try
        {
            var t = AccessTools.TypeByName("FireteamRoles.Radar");
            var m = t != null ? AccessTools.Method(t, "Draw") : null;
            if (m == null) return;
            _radarHidden = true;
            _h.Patch(m, prefix: new HarmonyMethod(typeof(Map), nameof(RadarPre)));
        }
        catch (Exception e) { _radarHidden = true; Plugin.Out.LogInfo("[Map] radar: " + e.Message); }
    }

    public static bool RadarPre() => On == null || !On.Value;

    internal static void Tick()
    {
        HideRadar();
        string scene = "";
        try { scene = SceneManager.GetActiveScene().name; } catch { }
        if (scene != _scene)
        {
            _scene = scene;
            foreach (var l in _layers.Values) if (l.Tex != null) UnityEngine.Object.Destroy(l.Tex);
            _layers.Clear(); _cur = null; Doors.Clear(); Mates.Clear(); _read = false; _nextPms = 0f;
        }
        var pn = Game.Pn();
        if (pn == null) return;
        if (!_read || (_reread > 0f && Time.unscaledTime >= _reread))
        {
            _reread = _read ? 0f : Time.unscaledTime + 15f;
            _read = true;
            Doors.Clear();
            try { ReadDoors(); } catch (Exception e) { Plugin.Out.LogInfo("[Map] doors: " + e.Message); }
        }
        if (On == null || !On.Value) return;
        Vector3 me = pn.transform.position;
        if (Time.unscaledTime >= _nextMates) { _nextMates = Time.unscaledTime + 0.5f; ReadMates(pn); }
        Scan(me);
    }

    static Layer LayerFor(Vector3 me)
    {
        _ground = Ground(me);
        int band = Mathf.RoundToInt(_ground / 3f);
        if (_cur != null && band == _band) return _cur;
        _band = band;
        if (!_layers.TryGetValue(band, out var l))
        {
            if (_layers.Count >= 4)
            {
                int old = 0; float t = float.MaxValue;
                foreach (var kv in _layers) if (kv.Value.Used < t) { t = kv.Value.Used; old = kv.Key; }
                if (_layers[old].Tex != null) UnityEngine.Object.Destroy(_layers[old].Tex);
                _layers.Remove(old);
            }
            l = new Layer();
            l.Tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            l.Tex.SetPixels32(l.Px); l.Tex.Apply(false);
            _layers[band] = l;
        }
        _far = 0; _near = 0;
        return _cur = l;
    }

    static int Slot(int c) => ((c % N) + N) % N;

    static void Forget(Layer l, int pcx, int pcz)
    {
        if (l.Pcx == int.MinValue || Mathf.Abs(pcx - l.Pcx) >= N || Mathf.Abs(pcz - l.Pcz) >= N)
        {
            Array.Clear(l.S, 0, l.S.Length);
            for (int i = 0; i < l.Px.Length; i++) l.Px[i] = Colors[0];
        }
        else
        {
            int h = N / 2;
            for (int c = l.Pcx; c != pcx; c += pcx > c ? 1 : -1)
            {
                int gone = Slot(pcx > c ? c + 1 + h : c - 1 - h);
                for (int z = 0; z < N; z++) { l.S[z * N + gone] = 0; l.Px[z * N + gone] = Colors[0]; }
            }
            for (int c = l.Pcz; c != pcz; c += pcz > c ? 1 : -1)
            {
                int gone = Slot(pcz > c ? c + 1 + h : c - 1 - h);
                for (int x = 0; x < N; x++) { l.S[gone * N + x] = 0; l.Px[gone * N + x] = Colors[0]; }
            }
        }
        l.Pcx = pcx; l.Pcz = pcz; l.Dirty = true;
    }

    static void Scan(Vector3 me)
    {
        var l = LayerFor(me);
        l.Used = Time.unscaledTime;
        int pcx = Mathf.FloorToInt(me.x / Cell), pcz = Mathf.FloorToInt(me.z / Cell);
        if (pcx != l.Pcx || pcz != l.Pcz) Forget(l, pcx, pcz);
        int nearCount = (2 * NearReach + 1) * (2 * NearReach + 1);
        _sw.Restart();
        int n = 0;
        while (_sw.ElapsedTicks < Stopwatch.Frequency / 830)
        {
            int i;
            if ((n++ & 1) == 0) { i = _near; _near = (_near + 1) % Mathf.Min(nearCount, _order.Length); }
            else { i = _far; _far = (_far + 1) % _order.Length; }
            int cx = pcx + _order[i].x, cz = pcz + _order[i].y;
            byte s = Probe((cx + 0.5f) * Cell, (cz + 0.5f) * Cell, _order[i].sqrMagnitude > 4);
            int k = Slot(cz) * N + Slot(cx);
            if (l.S[k] != s) { l.S[k] = s; l.Px[k] = Colors[s]; l.Dirty = true; }
        }
        if (l.Dirty && Time.unscaledTime >= _nextApply)
        {
            _nextApply = Time.unscaledTime + 0.1f;
            l.Dirty = false;
            l.Tex.SetPixels32(l.Px); l.Tex.Apply(false);
        }
    }

    static byte Probe(float x, float z, bool walls)
    {
        try
        {
            if (!Physics.Raycast(new Vector3(x, _ground + 2.2f, z), Vector3.down, out var hit, 4f, -5, QueryTriggerInteraction.Ignore)) return Unknown;
            float h = hit.point.y - _ground;
            if (h > 1.0f) return High;
            var chest = new Vector3(x, _ground + 1.35f, z);
            if (walls && Physics.CheckBox(chest, new Vector3(Cell * 0.5f, 0.5f, Cell * 0.5f), Quaternion.identity, -5, QueryTriggerInteraction.Ignore) && !OnlyBodies(chest)) return Wall;
            if (h < -1.0f) return Low;
            return Physics.Raycast(hit.point + Vector3.up * 0.3f, Vector3.up, 12f, -5, QueryTriggerInteraction.Ignore) ? In : Out;
        }
        catch { return Unknown; }
    }

    static bool OnlyBodies(Vector3 at)
    {
        var hits = Physics.OverlapBox(at, new Vector3(Cell * 0.5f, 0.5f, Cell * 0.5f), Quaternion.identity, -5, QueryTriggerInteraction.Ignore);
        if (hits == null) return false;
        for (int i = 0; i < hits.Length; i++)
            if (hits[i] != null && hits[i].GetComponentInParent<Health>() == null && hits[i].GetComponentInParent<PlayerNetworking>() == null) return false;
        return true;
    }

    internal static PlayerMaster[] Masters()
    {
        if (Time.unscaledTime >= _nextPms)
        {
            _nextPms = Time.unscaledTime + 1f;
            try
            {
                var l = PlayerMaster.AllPlayerMasters;
                if (l != null) { var a = new PlayerMaster[l.Count]; for (int i = 0; i < a.Length; i++) a[i] = l[i]; _pms = a; }
            }
            catch { }
        }
        return _pms;
    }
    static PlayerMaster[] _pms = new PlayerMaster[0];

    static void ReadMates(PlayerNetworking mine)
    {
        Mates.Clear();
        int team = Game.MyTeam();
        try
        {
            foreach (var pm in Masters())
            {
                try
                {
                    if (pm == null || pm == PlayerMaster.MyPlayerMaster) continue;
                    var pn = pm.PlayerSpawnedObject;
                    if (pn == null || pn.Pointer == mine.Pointer || !pn.isActiveAndEnabled) continue;
                    var tid = pm.MyTeamIdentifier;
                    if (tid != null && team != int.MinValue && tid.TeamID != team) continue;
                    var hp = pn.GetComponentInChildren<Health>();
                    if (hp != null && hp.isDead) continue;
                    Mates.Add(new Mate { P = pn.transform.position, Name = pm.thisPlayerName ?? "" });
                }
                catch { }
            }
        }
        catch { }
    }

    internal static float Free(Vector3 from, Vector3 dir, float max)
    {
        try { if (Physics.Raycast(from, dir, out var hit, max, -5, QueryTriggerInteraction.Ignore)) return hit.distance; } catch { }
        return max;
    }

    static float Ground(Vector3 p)
    {
        float d = Free(p + Vector3.up * 1.2f, Vector3.down, 3.5f);
        return d < 3.5f ? p.y + 1.2f - d : p.y;
    }

    static bool Frame(DoorV2 d, out Vector3 c, out Vector3 h)
    {
        c = h = Vector3.zero;
        Il2CppPathfinding.NodeLink2 link = null;
        try { link = d.DoorWalkableNavLink; } catch { }
        if (link == null) try { link = d.DoorOpenableNavLink; } catch { }
        if (link == null) return false;
        Transform a = null, b = null;
        try { a = link.StartTransform; b = link.EndTransform; } catch { }
        if (a == null) a = link.transform;
        if (b == null) try { b = link.end; } catch { }
        if (a == null || b == null) return false;
        c = (a.position + b.position) * 0.5f;
        h = b.position - a.position; h.y = 0f;
        if (h.sqrMagnitude < 0.01f) return false;
        h.Normalize();
        return true;
    }

    static void ReadDoors()
    {
        foreach (var d in UnityEngine.Object.FindObjectsOfType<DoorV2>())
            if (d != null && Frame(d, out var c, out var h)) Doors.Add(new Door { C = c, H = h, D = d });
        int real = Doors.Count;
        DrillHouse.Doors(_scene, Doors);
        Plugin.Out.LogInfo("[Map] " + real + " doors, " + (Doors.Count - real) + " drill house doorways");
    }

    internal static bool ToCard(Vector3 w, out Vector2 g)
    {
        float top = _card.y, h = _card.height;
        g = new Vector2(_card.x + _card.width * 0.5f + (w.x - _cx) * _k, top + h * 0.5f - (w.z - _cz) * _k);
        return g.x > _card.x + 2f && g.x < _card.xMax - 2f && g.y > top + 2f && g.y < top + h - 2f;
    }

    static Texture2D Shape(bool arrow)
    {
        const int s = 32;
        var t = new Texture2D(s, s, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear };
        var px = new Color32[s * s];
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float u = (x + 0.5f) / s - 0.5f, v = (y + 0.5f) / s - 0.5f;
                bool inside = arrow ? v > -0.42f && v < 0.46f && Mathf.Abs(u) < (0.46f - v) * 0.5f && !(v < -0.15f && Mathf.Abs(u) < (-0.15f - v) * 0.9f) : u * u + v * v < 0.2f;
                px[y * s + x] = inside ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            }
        t.SetPixels32(px); t.Apply(false);
        return t;
    }

    internal static void Blip(Vector2 g, float size, Color c)
    {
        if (_dot == null) _dot = Shape(false);
        GUI.color = c;
        Ui.Tex(new Rect(g.x - size * 0.5f, g.y - size * 0.5f, size, size), _dot);
    }


    internal static void Draw()
    {
        if (On == null || !On.Value || Event.current == null || Event.current.type != EventType.Repaint) return;
        if (Cursor.lockState != CursorLockMode.Locked || Drone.Flying) return;
        var pn = Game.Pn(); if (pn == null || _cur == null) return;
        Vector3 me = pn.transform.position;
        float size = Mathf.Round(Mathf.Clamp(Screen.height * 0.27f, 220f, 340f));
        _card = new Rect(20f, Screen.height - size - 20f, size, size);
        float view = size;
        _k = view / 30f;
        _cx = me.x; _cz = me.z;
        float half = 15f;

        GUI.color = new Color(0.04f, 0.05f, 0.07f, 0.45f);
        Ui.Tex(_card, Texture2D.whiteTexture);
        GUI.color = Color.white;
        float span01 = half * 2f / Cell / N;
        Ui.TexUv(new Rect(_card.x, _card.y, view, view), _cur.Tex, new Rect((_cx / Cell - half / Cell) / N, (_cz / Cell - half / Cell) / N, span01, span01));

        foreach (var d in Doors)
        {
            if (d.D == null || Mathf.Abs(d.C.y - me.y) > 2f || !ToCard(d.C, out var g)) continue;
            float w = Mathf.Max(4f, 1.0f * _k);
            GUI.color = new Color(1f, 0.7f, 0.25f, 0.95f);
            Ui.TexTurned(new Rect(g.x - w * 0.5f, g.y - 1.5f, w, 3f), Texture2D.whiteTexture, Mathf.Atan2(d.H.z, d.H.x) * Mathf.Rad2Deg + 90f);
        }

        foreach (var h in EnemyDots())
            {
                Vector3 v; try { v = Game.PlaceOf(h); } catch { continue; }
                if (!ToCard(v, out var g)) continue;
                Blip(g, 7f, new Color(1f, 0.25f, 0.2f, Mathf.Abs(v.y - me.y) < 2.5f ? 1f : 0.35f));
            }
        foreach (var m in Mates)
        {
            if (!ToCard(m.P, out var g)) continue;
            bool same = Mathf.Abs(m.P.y - me.y) < 2.5f;
            Blip(g, 8f, new Color(0.35f, 0.65f, 1f, same ? 1f : 0.35f));
            if (same && m.Name.Length > 0) { GUI.color = new Color(0.6f, 0.8f, 1f, 0.9f); Font5.Draw(g.x + 6f, g.y - 5f, m.Name.Length > 10 ? m.Name.Substring(0, 10) : m.Name, 0.8f); }
        }
        if (ToCard(me, out var mg))
        {
            if (_arrow == null) _arrow = Shape(true);
            var cam = Game.ViewCamera;
            var f = cam != null ? cam.transform.forward : pn.transform.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            GUI.color = new Color(1f, 0.85f, 0.2f, 1f);
            Ui.TexTurned(new Rect(mg.x - 8f, mg.y - 8f, 16f, 16f), _arrow, -yaw);
        }
        GUI.color = new Color(1f, 1f, 1f, 0.6f);
        Font5.Draw(_card.x + _card.width * 0.5f - 3f, _card.y + 4f, "N", 0.8f);
        GUI.color = Color.white;
    }
}
