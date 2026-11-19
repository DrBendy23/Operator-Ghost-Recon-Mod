using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace GhostReconView;

internal static class Playbook
{
    sealed class Aim
    {
        internal int K;
        internal Vector2 P;
        internal float[] U;
        internal Vector2[] Q;
    }

    sealed class Mv
    {
        internal float T0, Dur, Len;
        internal Vector2[] C;
        internal float[] Acc;
        internal Aim A;
    }

    sealed class Def
    {
        internal string Name, Scene;
        internal int Group;
        internal bool Open;
        internal Func<string> Desc;
        internal Func<List<List<Mv>>> Men;
    }

    internal static ConfigEntry<KeyCode> Key;
    internal static ConfigEntry<bool> Double;
    static float _tap = -9f;
    static bool _open, _paused;
    static int _sel = 4, _door = 1;
    static float _speed = 1f, _clock;
    static List<List<Mv>> _men;
    static float _end;
    static readonly List<(Rect r, Action a)> _hits = new List<(Rect, Action)>();
    static readonly Color[] Col = { new Color(0.95f, 0.55f, 0.3f), new Color(0.3f, 0.8f, 0.7f), new Color(0.7f, 0.6f, 1f), new Color(0.95f, 0.8f, 0.3f) };
    static readonly string[] Groups = { "BEFORE ENTRY", "ROOM ENTRY", "MOVEMENT" };
    static readonly string[] DoorName = { "OPEN DOORWAY", "OPENS IN, HINGE LEFT", "OPENS IN, HINGE RIGHT", "OPENS OUT, HINGE LEFT", "OPENS OUT, HINGE RIGHT" };
    static readonly int[] DoorT = { 0, 1, 1, -1, -1 };
    static readonly int[] DoorH = { 0, -1, 1, -1, 1 };
    static Def[] _defs;

    internal static bool Open => _open;

    internal static void Init(ConfigFile cfg)
    {
        Key = cfg.Bind("Map", "PlaybookKeyV2", KeyCode.Mouse4, "Opens the CQB playbook: every stack, entry and movement play, animated. Any key or mouse button; Mouse3 and Mouse4 are the side buttons. One press or Esc closes it.");
        Double = cfg.Bind("Map", "PlaybookDoubleClick", true, "On: double-click the key to open the playbook. Off: one press opens it.");
        _defs = Defs();
        Build();
    }

    static int T => DoorT[_door];
    static int Hd => DoorH[_door];
    static bool Outward => T == -1;
    static int SideFor() => T == 0 ? -1 : T == 1 ? Hd : -Hd;
    static string Lr(int s) => s < 0 ? "LEFT" : "RIGHT";
    static string Why() => T == 0 ? "Open doorway: stack on either side." : T == 1 ? "Opens in: stack on the hinge side (" + Lr(Hd) + "). The open door stands inside on that side, so its corner is cleared around the door." : "Opens out: stack on the knob side (" + Lr(-Hd) + ") and pull it fully open against the wall.";

    static Vector2 V(float x, float y) => new Vector2(x, y);
    static readonly Vector2 DL = new Vector2(-2.3f, 0.9f), DR = new Vector2(2.3f, 0.9f), SL = new Vector2(-1.2f, 1.6f), SR = new Vector2(1.2f, 1.6f);
    static Vector2 Deep(int s) => s < 0 ? DL : DR;
    static Vector2 Shrt(int s) => s < 0 ? SL : SR;
    static Vector2 HC(int s) => V(s * 3f, 0.1f);
    static Vector2 Far(int s) => V(s * 3f, 4.2f);
    static readonly Vector2 DoorP = new Vector2(0f, 0.3f), Rear = new Vector2(0f, -4f);
    static Vector2 Stk(int s, int m) => V(s * (0.8f + 0.7f * m), -0.5f - 0.15f * m);
    static Vector2 Spl(int m) { int r = m / 2, o = m % 2 == 1 ? 1 : -1; return V(o * (0.8f + 0.55f * r), -0.5f - 0.45f * r); }
    static int Own(Vector2 p) => p.x < 0f ? -1 : 1;
    static Vector2 WaitAim(int m) => m == 3 ? Rear : DoorP;
    static bool BehindDoor(int go) => T == 1 && go == Hd;

    static Aim At(Vector2 p) => new Aim { K = 1, P = p };
    static Aim At(float x, float y) => At(V(x, y));
    static readonly Aim Travel = new Aim { K = 0 }, Back = new Aim { K = 2 };
    static Aim Keys(params (float u, Vector2 p)[] k)
    {
        var a = new Aim { K = 3, U = new float[k.Length], Q = new Vector2[k.Length] };
        for (int i = 0; i < k.Length; i++) { a.U[i] = k[i].u; a.Q[i] = k[i].p; }
        return a;
    }
    static Aim Jamb(Vector2 j) => new Aim { K = 4, P = j };

    static Vector2 Cr(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    static Mv M(float t0, Aim aim, float hold, params Vector2[] pts)
    {
        var c = new List<Vector2>();
        for (int i = 0; i < pts.Length - 1; i++)
        {
            var p0 = pts[Math.Max(0, i - 1)];
            var p3 = pts[Math.Min(pts.Length - 1, i + 2)];
            for (int k = 0; k < 16; k++) c.Add(Cr(p0, pts[i], pts[i + 1], p3, k / 16f));
        }
        c.Add(pts[pts.Length - 1]);
        var acc = new float[c.Count];
        for (int i = 1; i < c.Count; i++) acc[i] = acc[i - 1] + Vector2.Distance(c[i], c[i - 1]);
        float len = acc[acc.Length - 1];
        return new Mv { T0 = t0, C = c.ToArray(), Acc = acc, Len = len, A = aim, Dur = len < 0.01f ? hold : 0.5f + len / 1.9f };
    }

    static List<Mv> Man(params Mv[] mv) => new List<Mv>(mv);

    static List<List<Mv>> Entry(Vector2[] starts, int[] goes, bool hook)
    {
        var men = new List<List<Mv>>();
        for (int m = 0; m < 4; m++)
        {
            var s = starts[m];
            int go = goes[m], o = Own(s);
            var end = m < 2 ? Deep(go) : Shrt(go);
            var via = hook && go == o ? V(o * 0.3f, 0.42f) : V(o * 0.1f, 0.3f);
            var pts = new List<Vector2> { s, via };
            if (BehindDoor(go)) pts.Add(V(go * 0.3f, 1.4f));
            pts.Add(end);
            var aim = m < 2 ? Keys((0f, HC(go)), (0.6f, HC(go)), (1f, V(-go * 1.2f, 4.2f))) : Keys((0f, V(0f, 2.5f)), (0.45f, Far(go)), (1f, V(go * 0.3f, 4.2f)));
            men.Add(Man(M(0f, At(WaitAim(m)), 0f, s, s), M(0.9f + m * 0.55f, aim, 0f, pts.ToArray())));
        }
        return men;
    }

    static int[] Alt(int f) => new[] { f, -f, f, -f };
    static Vector2[] Stack(int s) => new[] { Stk(s, 0), Stk(s, 1), Stk(s, 2), Stk(s, 3) };
    static Vector2[] Split() => new[] { Spl(0), Spl(1), Spl(2), Spl(3) };

    static List<List<Mv>> SplitOrKnob(bool hook)
    {
        if (Outward) { int s = SideFor(); return Entry(Stack(s), Alt(hook ? s : -s), hook); }
        return Entry(Split(), hook ? new[] { -1, 1, -1, 1 } : new[] { 1, -1, 1, -1 }, hook);
    }

    static List<List<Mv>> Formation(Func<int, Vector2> pos, float spread)
    {
        var men = new List<List<Mv>>();
        for (int m = 0; m < 4; m++)
        {
            var p = pos(m);
            men.Add(Man(M(m * 0.35f, Travel, 0f, V(p.x * spread, -3.4f), V(p.x * (spread > 1.5f ? 1.4f : 1f) + (spread > 1.5f ? 0f : Mathf.Sign(p.x) * 0f), -1.6f), p), M(3.6f, At(WaitAim(m)), 0.6f, p, p)));
        }
        return men;
    }

    static List<List<Mv>> Walk(params (float t0, Aim aim, Vector2[] pts)[] men)
    {
        var list = new List<List<Mv>>();
        foreach (var w in men) list.Add(Man(M(w.t0, w.aim, 0f, w.pts)));
        return list;
    }

    static Def[] Defs()
    {
        const string Out = "  Opens out: a split stack would stand in the door's swing, so the team stacks on the knob side and runs it as a ";
        return new[]
        {
            new Def { Name = "STACK LEFT", Group = 0, Scene = "room", Desc = () => "Formation only: team on the left wall, #1 at the door, #4 covering the rear.", Men = () => Formation(m => Stk(-1, m), 1.1f) },
            new Def { Name = "STACK RIGHT", Group = 0, Scene = "room", Desc = () => "Formation only: team on the right wall, #1 at the door, #4 covering the rear.", Men = () => Formation(m => Stk(1, m), 1.1f) },
            new Def { Name = "SPLIT STACK", Group = 0, Scene = "room", Desc = () => Outward ? "Opens out: don't split. The door would swing into the hinge-side men, so the team stacks on the knob side (" + Lr(SideFor()) + ")." : "Formation only: a V on both sides, #1 and #2 at the edges. Only cross a closed door to set it.", Men = () => Formation(m => Outward ? Stk(SideFor(), m) : Spl(m), 2.2f) },
            new Def { Name = "CROSSOVER", Group = 1, Scene = "room", Open = true, Desc = () => "#1 goes away from his wall, gun on the hard corner he walks toward, then works across. #2 goes the opposite way and does the same. #3 and #4 center check, deep corner, then inward.", Men = () => { int s = SideFor(); return Entry(Stack(s), Alt(-s), false); } },
            new Def { Name = "BUTTON HOOK", Group = 1, Scene = "room", Open = true, Desc = () => "#1 U-turns back along his own wall, gun on the hard corner right behind that wall, then works across. #2 crosses, then alternate." + (T == 1 ? "  This door opens in, so #1's corner is behind the door. Crossover is usually the better call here." : ""), Men = () => { int s = SideFor(); return Entry(Stack(s), Alt(s), true); } },
            new Def { Name = "CRISS-CROSS", Group = 1, Scene = "room", Open = true, Desc = () => Outward ? "From the knob-side stack." + Out + "crossover." : "From a split stack, each side crosses to the far corner, gun on that hard corner first.", Men = () => SplitOrKnob(false) },
            new Def { Name = "SPLIT HOOK", Group = 1, Scene = "room", Open = true, Desc = () => Outward ? "From the knob-side stack." + Out + "button hook." : "From a split stack, each side hooks to its own corner, gun on the hard corner first.", Men = () => SplitOrKnob(true) },
            new Def { Name = "LIMITED PENETRATION", Group = 1, Scene = "room", Open = true, Desc = () => "#1 and #2 step onto the threshold and clear the opposite near corner from there, then sweep deep. Nobody commits inside.", Men = Limpen },
            new Def { Name = "SLICE THE PIE", Group = 1, Scene = "room", Open = true, Desc = () => "Arc around the doorway, gun riding the edge of the frame as each slice opens. Then cross fast, gun on the hard corner along that wall, and sweep across to the other one.", Men = Pie },
            new Def { Name = "PEEK", Group = 1, Scene = "room", Open = true, Desc = () => "From the stack side edge, lean out with the gun across the room to read its shape, then back to pick the entry.", Men = Peek },
            new Def { Name = "HALLWAY ONLINE", Group = 2, Scene = "hall", Desc = () => "#1 and #2 abreast on each wall covering down the hall, #3 behind, #4 walks backward on the rear.", Men = () => Walk((0.5f, At(0.3f, 9f), new[] { V(-0.65f, -5f), V(-0.65f, 4f) }), (0.5f, At(-0.3f, 9f), new[] { V(0.65f, -5f), V(0.65f, 4f) }), (0.5f, At(0f, 9f), new[] { V(-0.3f, -6f), V(-0.3f, 3f) }), (0.5f, Back, new[] { V(0.3f, -6.8f), V(0.3f, 2.2f) })) },
            new Def { Name = "SERPENTINE FILE", Group = 2, Scene = "hall", Desc = () => "Narrow hall: staggered file alternating walls, each man a different angle, last man faces back.", Men = () => Walk((0.5f, At(-0.4f, 9f), new[] { V(0.6f, -4f), V(0.6f, 4.5f) }), (0.5f, At(0.6f, 9f), new[] { V(-0.6f, -4.8f), V(-0.6f, 3.7f) }), (0.5f, At(-1f, 8f), new[] { V(0.6f, -5.6f), V(0.6f, 2.9f) }), (0.5f, Back, new[] { V(-0.6f, -6.4f), V(-0.6f, 2.1f) })) },
            new Def { Name = "ROLLING T", Group = 2, Scene = "wide", Desc = () => "Wide hall: two points abreast, leader centre behind, rear guard walking backward.", Men = () => Walk((0.5f, At(0f, 9f), new[] { V(-1.1f, -4f), V(-1.1f, 4f) }), (0.5f, At(0f, 9f), new[] { V(1.1f, -4f), V(1.1f, 4f) }), (0.5f, At(0f, 9f), new[] { V(0f, -5f), V(0f, 3f) }), (0.5f, Back, new[] { V(0f, -6f), V(0f, 2f) })) },
            new Def { Name = "T-INTERSECTION", Group = 2, Scene = "T", Desc = () => "Stack both walls, cross cover the far side, then #1 and #2 snap out together left and right.", Men = () => new List<List<Mv>> { Man(M(0.4f, At(3f, 4f), 0f, V(-0.65f, -4f), V(-0.65f, 2.4f)), M(4.2f, At(-8f, 4f), 0f, V(-0.65f, 2.4f), V(-0.5f, 3.8f))), Man(M(0.4f, At(-3f, 4f), 0f, V(0.65f, -4f), V(0.65f, 2.4f)), M(4.2f, At(8f, 4f), 0f, V(0.65f, 2.4f), V(0.5f, 3.8f))), Man(M(0.7f, At(0f, 4f), 0f, V(-0.65f, -5f), V(-0.65f, 1.6f))), Man(M(0.9f, Back, 0f, V(0.65f, -5.6f), V(0.65f, 1f))) } },
            new Def { Name = "L-CORNER HIGH-LOW", Group = 2, Scene = "Lc", Desc = () => "#1 low and #2 high at the corner, push around together. #4 walks backward the whole way covering the rear.", Men = () => new List<List<Mv>> { Man(M(0.4f, At(3f, 4f), 0f, V(0.6f, -4f), V(0.6f, 2.5f)), M(4f, At(9f, 4f), 0f, V(0.6f, 2.5f), V(2f, 3.9f), V(5f, 3.9f))), Man(M(0.6f, At(3f, 4.2f), 0f, V(0.25f, -4.6f), V(0.25f, 2.2f)), M(4.2f, At(9f, 4.3f), 0f, V(0.25f, 2.2f), V(1.6f, 4.3f), V(4.5f, 4.3f))), Man(M(0.9f, At(0f, 5f), 0f, V(-0.4f, -5.2f), V(-0.4f, 1.6f)), M(4.6f, At(9f, 3.6f), 0f, V(-0.4f, 1.6f), V(1f, 3.6f), V(3.4f, 3.6f))), Man(M(1.1f, Back, 0f, V(-0.6f, -6f), V(-0.6f, 1f)), M(5f, Back, 0f, V(-0.6f, 1f), V(0f, 3f), V(2f, 3.5f))) } },
            new Def { Name = "LEAPFROG (BOUNDING OVERWATCH)", Group = 2, Scene = "open", Desc = () => "One pair stays set covering forward while the other pair moves past them to the next cover, then they swap.", Men = () => new List<List<Mv>> { Man(M(2.6f, At(0f, 9f), 0f, V(-1.4f, -4f), V(-1.4f, 0.5f)), M(6.6f, At(0f, 9f), 0f, V(-1.4f, 0.5f), V(-1.4f, 5f))), Man(M(2.6f, At(0.5f, 9f), 0f, V(-0.8f, -4f), V(-0.8f, 0.5f)), M(6.6f, At(0.5f, 9f), 0f, V(-0.8f, 0.5f), V(-0.8f, 5f))), Man(M(0.6f, At(0f, 9f), 0f, V(1.1f, -6f), V(1.1f, -2f)), M(4.6f, At(0f, 9f), 0f, V(1.1f, -2f), V(1.1f, 2.6f))), Man(M(0.6f, At(0.5f, 9f), 0f, V(1.7f, -6f), V(1.7f, -2f)), M(4.6f, At(0.5f, 9f), 0f, V(1.7f, -2f), V(1.7f, 2.6f))) } },
            new Def { Name = "STRAIGHT STAIRS", Group = 2, Scene = "stairs", Desc = () => "#1 far side and #2 near side climb abreast, #3 between them covering the top landing, #4 backward on the rear.", Men = () => Walk((0.5f, At(-0.3f, 8f), new[] { V(0.5f, -2.5f), V(0.55f, 4.6f) }), (0.5f, At(0.3f, 8f), new[] { V(-0.5f, -2.5f), V(-0.55f, 4.6f) }), (0.7f, At(0f, 8f), new[] { V(0f, -3.4f), V(0f, 3.8f) }), (0.9f, Back, new[] { V(0f, -4.2f), V(0f, 3f) })) },
            new Def { Name = "SWITCHBACK STAIRS", Group = 2, Scene = "switch", Desc = () => "#1 up the outside edge covering the landing above, #2 right behind covering up and back, #3 the far corner, #4 the rear. Repeat each flight.", Men = () => Walk((0.5f, Travel, new[] { V(1.4f, -3f), V(1.4f, 2.6f), V(0.6f, 3.6f), V(-1.2f, 3.6f) }), (0.8f, At(-1.2f, 4f), new[] { V(1.4f, -3.8f), V(1.4f, 1.8f), V(1.2f, 2.8f) }), (1.1f, At(-1.5f, 4f), new[] { V(0.9f, -4.4f), V(0.9f, 1.2f) }), (1.3f, Back, new[] { V(0.9f, -5.2f), V(0.9f, 0.4f) })) },
        };
    }

    static List<List<Mv>> Limpen()
    {
        int s = SideFor();
        Vector2 a = Outward ? Stk(s, 0) : Spl(0), b = Outward ? Stk(s, 1) : Spl(1), c = Outward ? Stk(s, 2) : Spl(2), d = Outward ? Stk(s, 3) : Spl(3);
        Vector2 ta = V(-0.42f, 0.12f), tb = V(0.42f, 0.12f);
        return new List<List<Mv>>
        {
            Man(M(0f, At(DoorP), 0f, a, a), M(0.9f, Keys((0f, DoorP), (1f, HC(1))), 0f, a, ta), M(2.6f, Keys((0f, HC(1)), (1f, Far(1))), 1.4f, ta, ta)),
            Man(M(0f, At(DoorP), 0f, b, b), M(1.2f, Keys((0f, DoorP), (1f, HC(-1))), 0f, b, tb), M(2.9f, Keys((0f, HC(-1)), (1f, Far(-1))), 1.4f, tb, tb)),
            Man(M(0f, At(DoorP), 0f, c, c)),
            Man(M(0f, At(Rear), 0f, d, d)),
        };
    }

    static List<List<Mv>> Pie()
    {
        int s = SideFor();
        var jamb = V(s * 0.55f, 0f);
        var arc = new List<Vector2> { Stk(s, 0) };
        for (int a = 65; a >= -70; a -= 15) { float r = a * Mathf.Deg2Rad; arc.Add(V(s * 2.2f * Mathf.Sin(r), -2.2f * Mathf.Cos(r))); }
        arc.Add(V(-s * 0.45f, -0.5f));
        var inPts = new List<Vector2> { V(-s * 0.45f, -0.5f), V(-s * 0.4f, 0.35f) };
        if (BehindDoor(-s)) inPts.Add(V(-s * 0.3f, 1.4f));
        inPts.Add(Deep(-s));
        return new List<List<Mv>>
        {
            Man(M(0f, At(DoorP), 0f, Stk(s, 0), Stk(s, 0)), M(0.6f, Jamb(jamb), 0f, arc.ToArray()), M(5.6f, Keys((0f, HC(-s)), (0.6f, HC(-s)), (1f, HC(s))), 0f, inPts.ToArray())),
            Man(M(0f, At(DoorP), 0f, Stk(s, 1), Stk(s, 1))),
            Man(M(0f, At(DoorP), 0f, Stk(s, 2), Stk(s, 2))),
            Man(M(0f, At(Rear), 0f, Stk(s, 3), Stk(s, 3))),
        };
    }

    static List<List<Mv>> Peek()
    {
        int s = SideFor();
        var e = V(s * 0.55f, -0.32f);
        return new List<List<Mv>>
        {
            Man(M(0f, At(DoorP), 0f, Stk(s, 0), Stk(s, 0)), M(0.6f, Keys((0f, DoorP), (1f, V(-s * 2.6f, 1.5f))), 0f, Stk(s, 0), e), M(1.9f, Keys((0f, V(-s * 2.6f, 1.5f)), (1f, V(-s * 1f, 4f))), 0.8f, e, e), M(3f, At(DoorP), 0f, e, Stk(s, 0))),
            Man(M(0f, At(DoorP), 0f, Stk(s, 1), Stk(s, 1))),
            Man(M(0f, At(DoorP), 0f, Stk(s, 2), Stk(s, 2))),
            Man(M(0f, At(Rear), 0f, Stk(s, 3), Stk(s, 3))),
        };
    }

    static void Build()
    {
        _men = _defs[_sel].Men();
        _end = 0f;
        foreach (var man in _men) { var l = man[man.Count - 1]; _end = Mathf.Max(_end, l.T0 + l.Dur); }
        _clock = 0f;
    }

    static float Ease(float u) => u < 0.5f ? 4f * u * u * u : 1f - Mathf.Pow(-2f * u + 2f, 3f) / 2f;

    static Vector2 PosOn(Mv mv, float d)
    {
        if (mv.Len < 0.01f) return mv.C[0];
        float want = Mathf.Clamp01(d) * mv.Len;
        int i = 1;
        while (i < mv.Acc.Length - 1 && mv.Acc[i] < want) i++;
        float a = mv.Acc[i - 1], b = mv.Acc[i], q = b > a ? (want - a) / (b - a) : 0f;
        return Vector2.Lerp(mv.C[i - 1], mv.C[i], q);
    }

    static float Ang(Vector2 p, Vector2 q) => Mathf.Atan2(q.y - p.y, q.x - p.x);

    static float AimAng(Aim a, Vector2 p, Vector2? dir, float u)
    {
        switch (a.K)
        {
            case 1: return Ang(p, a.P);
            case 2: return dir.HasValue ? Mathf.Atan2(-dir.Value.y, -dir.Value.x) : -Mathf.PI / 2f;
            case 3:
            {
                int i = 1;
                while (i < a.U.Length - 1 && a.U[i] < u) i++;
                float t = a.U[i] > a.U[i - 1] ? Mathf.Clamp01((u - a.U[i - 1]) / (a.U[i] - a.U[i - 1])) : 1f;
                return Mathf.LerpAngle(Ang(p, a.Q[i - 1]) * Mathf.Rad2Deg, Ang(p, a.Q[i]) * Mathf.Rad2Deg, t) * Mathf.Deg2Rad;
            }
            case 4: return Ang(p, p + (a.P - p) * 4f);
            default: return dir.HasValue ? Mathf.Atan2(dir.Value.y, dir.Value.x) : Mathf.PI / 2f;
        }
    }

    static void State(List<Mv> man, float t, out Vector2 p, out float ang)
    {
        p = man[0].C[0];
        Vector2? dir = null;
        var cur = man[0];
        float u = 0f;
        foreach (var mv in man)
        {
            if (t < mv.T0) break;
            cur = mv;
            u = mv.Dur > 0f ? Mathf.Min(1f, (t - mv.T0) / mv.Dur) : 1f;
            float e = Ease(u);
            p = PosOn(mv, e);
            if (mv.Len > 0.01f) { var a = PosOn(mv, e - 0.02f); var b = PosOn(mv, e + 0.02f); dir = b - a; } else dir = null;
            if (u < 1f) break;
        }
        ang = AimAng(cur.A, p, dir, Ease(u));
    }

    internal static void Tick()
    {
        if (Key == null) return;
        bool down = Key.Value != KeyCode.None && Input.GetKeyDown(Key.Value);
        if (!_open)
        {
            if (!down) return;
            if (Double.Value && Time.unscaledTime - _tap > 0.35f) { _tap = Time.unscaledTime; return; }
            _tap = -9f;
            if (Game.Pn() != null && Cursor.lockState == CursorLockMode.Locked && !Drone.Flying && !Panel.Open) { _open = true; Build(); }
            return;
        }
        if (down || Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Drone.Freeze = true;
        if (Input.GetKeyDown(KeyCode.RightArrow)) { _sel = (_sel + 1) % _defs.Length; Build(); }
        if (Input.GetKeyDown(KeyCode.LeftArrow)) { _sel = (_sel + _defs.Length - 1) % _defs.Length; Build(); }
        if (Input.GetKeyDown(KeyCode.D)) { _door = (_door + 1) % DoorName.Length; Build(); }
        if (Input.GetKeyDown(KeyCode.Space)) _paused = !_paused;
        if (!_paused) _clock += Time.unscaledDeltaTime * _speed;
        if (_clock > _end + 1.85f) _clock = 0f;
    }

    static void Close()
    {
        _open = false;
        Drone.Freeze = false;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    static float _ox, _oy, _k;
    static Vector2 S(Vector2 w) => new Vector2(_ox + w.x * _k, _oy - w.y * _k);

    static void Box(Rect r, Color c) { GUI.color = c; Ui.Tex(r, Texture2D.whiteTexture); }

    static void Line(Vector2 a, Vector2 b, float th, Color c)
    {
        var d = b - a;
        float len = d.magnitude;
        if (len < 0.5f) return;
        GUI.color = c;
        var mid = (a + b) * 0.5f;
        Ui.TexTurned(new Rect(mid.x - len * 0.5f, mid.y - th * 0.5f, len, th), Texture2D.whiteTexture, -Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
    }

    static void Floor(float x0, float y0, float x1, float y1, Color c)
    {
        var a = S(V(x0, y1)); var b = S(V(x1, y0));
        Box(new Rect(a.x, a.y, b.x - a.x, b.y - a.y), c);
    }

    static readonly Color Fl = new Color(0.23f, 0.26f, 0.31f, 0.75f), Ha = new Color(0.17f, 0.19f, 0.22f, 0.7f), Wl = new Color(0.89f, 0.9f, 0.93f), Wood = new Color(0.79f, 0.64f, 0.42f);

    static void Walls(float th, params float[] seg)
    {
        for (int i = 0; i + 3 < seg.Length; i += 4) Line(S(V(seg[i], seg[i + 1])), S(V(seg[i + 2], seg[i + 3])), th, Wl);
    }

    static void DrawDoor(float u)
    {
        if (T == 0) return;
        float hx = Hd * 0.55f, a0 = Hd < 0 ? 0f : Mathf.PI, full = T == 1 ? Mathf.PI / 2f : Mathf.PI;
        float a = a0 + T * (Hd < 0 ? 1 : -1) * u * full, hy = T == -1 ? -0.07f * u : 0f;
        var h = V(hx, hy);
        Line(S(h), S(h + V(Mathf.Cos(a), Mathf.Sin(a)) * 1.1f), 4f, Wood);
        Map.Blip(S(h), 7f, Wood);
    }

    static void Scene(string s, float u)
    {
        float w = 4f;
        switch (s)
        {
            case "room":
                Floor(-7.5f, -4.5f, 7.5f, 0f, Ha); Floor(-3f, 0f, 3f, 4.2f, Fl);
                Walls(w, -7.5f, 0f, -0.55f, 0f, 0.55f, 0f, 7.5f, 0f, -3f, 0f, -3f, 4.2f, -3f, 4.2f, 3f, 4.2f, 3f, 4.2f, 3f, 0f);
                DrawDoor(u);
                break;
            case "hall": Floor(-1f, -8f, 1f, 7f, Fl); Walls(w, -1f, -8f, -1f, 7f, 1f, -8f, 1f, 7f); break;
            case "wide": Floor(-1.8f, -8f, 1.8f, 7f, Fl); Walls(w, -1.8f, -8f, -1.8f, 7f, 1.8f, -8f, 1.8f, 7f); break;
            case "T": Floor(-1f, -8f, 1f, 3f, Fl); Floor(-9f, 3f, 9f, 5f, Fl); Walls(w, -1f, -8f, -1f, 3f, -1f, 3f, -9f, 3f, 1f, -8f, 1f, 3f, 1f, 3f, 9f, 3f, -9f, 5f, 9f, 5f); break;
            case "Lc": Floor(-1f, -8f, 1f, 5f, Fl); Floor(1f, 3f, 9f, 5f, Fl); Walls(w, -1f, -8f, -1f, 5f, -1f, 5f, 9f, 5f, 1f, -8f, 1f, 3f, 1f, 3f, 9f, 3f); break;
            case "open":
                Floor(-9f, -8f, 9f, 7f, new Color(0.18f, 0.2f, 0.24f, 0.7f));
                foreach (var c in new[] { V(-1.1f, -3.3f), V(1.4f, -1.3f), V(-1.1f, 1.2f), V(1.4f, 3.2f), V(-1.1f, 5.7f) }) Floor(c.x - 1f, c.y - 0.25f, c.x + 1f, c.y + 0.25f, new Color(0.39f, 0.42f, 0.48f));
                break;
            case "stairs":
                Floor(-1.1f, -5f, 1.1f, 5.5f, Fl);
                for (float y = -1f; y <= 4.2f; y += 0.4f) Line(S(V(-1.1f, y)), S(V(1.1f, y)), 1.5f, new Color(1f, 1f, 1f, 0.18f));
                Walls(w, -1.1f, -5f, -1.1f, 5.5f, 1.1f, -5f, 1.1f, 5.5f);
                break;
            case "switch":
                Floor(-2f, -6f, 2f, 4.4f, Fl);
                for (float y = -1f; y <= 2.6f; y += 0.4f) { Line(S(V(0.1f, y)), S(V(1.9f, y)), 1.5f, new Color(1f, 1f, 1f, 0.18f)); Line(S(V(-1.9f, y)), S(V(-0.1f, y)), 1.5f, new Color(1f, 1f, 1f, 0.18f)); }
                Walls(w, -2f, -6f, -2f, 4.4f, 2f, -6f, 2f, 4.4f, -2f, 4.4f, 2f, 4.4f, 0f, -1f, 0f, 2.8f);
                GUI.color = new Color(0.6f, 0.64f, 0.7f);
                var up = S(V(0.6f, 0.8f)); Font5.Draw(up.x, up.y, "UP", 0.9f);
                var lan = S(V(-0.5f, 3.8f)); Font5.Draw(lan.x, lan.y, "LANDING", 0.9f);
                break;
        }
    }

    static Vector3 Extent(string scene)
    {
        switch (scene)
        {
            case "room": return new Vector3(7.7f, -4.7f, 4.5f);
            case "hall": case "wide": return new Vector3(4f, -8.3f, 7.3f);
            case "stairs": return new Vector3(3f, -5.3f, 5.8f);
            case "switch": return new Vector3(3f, -6.3f, 4.7f);
            default: return new Vector3(9.3f, -8.3f, 7.3f);
        }
    }

    static void Pill(Rect r, string text, bool on, Action a)
    {
        Box(r, on ? new Color(1f, 0.85f, 0.2f, 0.25f) : new Color(1f, 1f, 1f, 0.06f));
        GUI.color = on ? new Color(1f, 0.85f, 0.2f) : new Color(1f, 1f, 1f, 0.85f);
        Font5.Draw(r.x + 10f, r.y + r.height * 0.5f - 6f, text, 1f);
        _hits.Add((r, a));
    }

    static List<string> Wrap(string text, float width)
    {
        var lines = new List<string>();
        int max = Mathf.Max(20, (int)(width / 8.5f));
        var line = "";
        foreach (var word in text.Split(' '))
        {
            if (word.Length == 0) continue;
            if (line.Length + word.Length + 1 > max) { lines.Add(line); line = word; }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    internal static void Draw()
    {
        if (!_open || Event.current == null) return;
        var ev = Event.current;
        if (ev.type == EventType.MouseDown && ev.button == 0)
        {
            var mp = ev.mousePosition;
            foreach (var h in _hits) if (h.r.Contains(mp)) { h.a(); ev.Use(); break; }
            return;
        }
        if (ev.type != EventType.Repaint) return;
        _hits.Clear();

        float W = Mathf.Min(Screen.width * 0.62f, 1000f), H = Mathf.Min(Screen.height - 80f, 640f);
        var panel = new Rect((Screen.width - W) * 0.5f, (Screen.height - H) * 0.5f, W, H);
        Box(panel, new Color(0.06f, 0.07f, 0.09f, 0.55f));
        GUI.color = Color.white;
        Font5.Draw(panel.x + 20f, panel.y + 16f, "CQB PLAYBOOK", 1.4f);
        GUI.color = new Color(1f, 1f, 1f, 0.5f);
        Font5.Draw(panel.x + 180f, panel.y + 20f, "CLICK A PLAY  -  ARROWS NEXT  -  D DOOR  -  SPACE PAUSE  -  " + Key.Value.ToString().ToUpperInvariant() + " OR ESC CLOSE", 0.9f);

        float lx = panel.x + 20f, ly = panel.y + 54f, lw = 230f;
        for (int g = 0; g < 3; g++)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.45f);
            Font5.Draw(lx, ly, Groups[g], 0.9f);
            ly += 20f;
            for (int i = 0; i < _defs.Length; i++)
            {
                if (_defs[i].Group != g) continue;
                int idx = i;
                Pill(new Rect(lx, ly, lw, 22f), _defs[i].Name, i == _sel, () => { _sel = idx; Build(); });
                ly += 25f;
            }
            ly += 8f;
        }

        float cx = lx + lw + 20f, cw = panel.xMax - 20f - cx;
        float cy = panel.y + 54f;
        var def = _defs[_sel];
        Pill(new Rect(cx, cy, 300f, 26f), "DOOR: " + DoorName[_door], false, () => { _door = (_door + 1) % DoorName.Length; Build(); });
        Pill(new Rect(cx + 310f, cy, 110f, 26f), _paused ? "PLAY" : "PAUSE", _paused, () => _paused = !_paused);
        Pill(new Rect(cx + 430f, cy, 120f, 26f), "SPEED " + (_speed == 1f ? "1X" : _speed == 0.5f ? "0.5X" : "1.5X"), false, () => _speed = _speed == 1f ? 0.5f : _speed == 0.5f ? 1.5f : 1f);

        var cap = Wrap(def.Desc() + (def.Scene == "room" ? "  " + Why() : ""), cw);
        float capH = cap.Count * 18f;
        var area = new Rect(cx, cy + 36f, cw, panel.yMax - 20f - (cy + 36f) - capH - 12f);
        Box(area, new Color(0.1f, 0.11f, 0.14f, 0.35f));
        GUI.color = new Color(1f, 1f, 1f, 0.75f);
        for (int i = 0; i < cap.Count; i++) Font5.Draw(cx, area.yMax + 10f + i * 18f, cap[i], 0.95f);

        var b = Extent(def.Scene);
        _k = Mathf.Min(area.height * 0.92f / (b.z - b.y), area.width * 0.92f / (b.x * 2f));
        _ox = area.center.x;
        _oy = area.center.y + (b.z + b.y) * 0.5f * _k;

        float hold = 1.4f, fade = 0.45f, alpha = 1f;
        if (_clock < fade) alpha = _clock / fade;
        else if (_clock > _end + hold) alpha = Mathf.Clamp01(1f - (_clock - _end - hold) / fade);
        float doorU = def.Open ? Mathf.Clamp01((_clock - 0.3f) / 0.55f) : 0f;
        Scene(def.Scene, doorU);

        for (int m = 0; m < _men.Count; m++)
        {
            var c = Col[m]; c.a = 0.28f * alpha;
            foreach (var mv in _men[m])
            {
                if (mv.Len < 0.01f) continue;
                for (float d = 0f; d <= mv.Len; d += 0.3f) Map.Blip(S(PosOn(mv, d / mv.Len)), 4f, c);
            }
        }
        float r = Mathf.Clamp(_k * 0.3f, 9f, 14f);
        for (int m = 0; m < _men.Count; m++)
        {
            State(_men[m], _clock, out var p, out var ang);
            var g = S(p);
            var dir = new Vector2(Mathf.Cos(ang), -Mathf.Sin(ang));
            var c = Col[m]; c.a = alpha;
            Line(g + dir * r, g + dir * (r * 1.85f), 3f, c);
            Map.Blip(g, r * 2f + 3f, new Color(1f, 1f, 1f, 0.55f * alpha));
            Map.Blip(g, r * 2f, c);
            GUI.color = new Color(0.08f, 0.09f, 0.11f, alpha);
            Font5.Draw(g.x - 4f, g.y - 7f, (m + 1).ToString(), 1f);
        }
        GUI.color = Color.white;
    }
}
