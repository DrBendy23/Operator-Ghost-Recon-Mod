using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace GhostReconView;

internal static class LaserBeams
{
    internal static ConfigEntry<bool> On;
    internal static ConfigEntry<float> Thickness, Strength, Paleness, Shimmer, Grain, DotSize;
    internal static ConfigEntry<int> ColorChoice;

    const float MaxDistance = 300f;
    const float LensOffset = 0.12f;
    const float FallbackWidth = 0.0014f;
    const float DotPixels = 3f;
    const float DotGlow = 0.5f;
    const float NightVisionDotGlow = 0.12f;
    const int DustSize = 512;
    const float MaxSpotAngle = 12f;
    const float FadeDistance = 15f;
    const float NightVisionFadeDistance = 300f;
    const float NightVisionBeamGlow = 0.1f;
    const float DayBeamGlow = 0.3f;
    const float DaylightDotDistance = 50f;
    static readonly string[] BaseColors = { "_Color", "_BaseColor", "_UnlitColor", "_TintColor", "_MainColor" };

    class Source { public Light light; public IRLaserLight part; public Transform emitter; public bool ir; public float range; }
    class Beam { public GameObject go; public LineRenderer lr, dot; public Material mat, dotMat; public bool used; public Vector3 lastDir; public float swing; }

    static FieldInfo fNightVision, fRoot;
    static Type _weaponType; static PropertyInfo _pEquipped; static Il2CppSystem.Type _weaponIl2;

    static readonly List<Source> _sources = new();
    static readonly Dictionary<IntPtr, Beam> _beams = new();
    static readonly Dictionary<IntPtr, Component> _weaponOf = new();
    static readonly Dictionary<IntPtr, bool> _onBeforeNv = new();
    static readonly Dictionary<IntPtr, (Light l, Color c)> _gameLights = new();
    static readonly Dictionary<IntPtr, LineRenderer> _hidden = new();
    static readonly HashSet<IntPtr> _hiddenNow = new();

    static Material _baseMat;
    static Texture2D _dust;
    static float[] _dustBase;
    static bool _dustFailed, _dustNv;
    static float _dustGrain = -1f;
    static float _nextScan;

    static bool NightVision
    {
        get
        {
            if (fNightVision != null) return (bool)fNightVision.GetValue(null);
            try { var gm = GameManager.instance; return gm != null && gm.nvgOn; } catch { return false; }
        }
    }

    internal static void Init(ConfigFile cfg)
    {
        On = cfg.Bind("Laser", "ShowBeam", true, "Show the laser beam.");
        Thickness = cfg.Bind("Laser", "LineThickness", 225f, new ConfigDescription("Laser thickness, percent.", new AcceptableValueRange<float>(100f, 270f)));
        Strength = cfg.Bind("Laser", "LineStrength", 115f, new ConfigDescription("Laser strength, percent. Above 100 adds a little self-brightness.", new AcceptableValueRange<float>(30f, 150f)));
        Paleness = cfg.Bind("Laser", "LinePaleness", 65f, new ConfigDescription("How pale the laser color is, percent.", new AcceptableValueRange<float>(20f, 70f)));
        Shimmer = cfg.Bind("Laser", "LineShimmer", 25f, new ConfigDescription("How much the laser shimmers when you move, percent.", new AcceptableValueRange<float>(10f, 40f)));
        Grain = cfg.Bind("Laser", "LineGrain", 105f, new ConfigDescription("How grainy the laser is (dust and cuts), percent.", new AcceptableValueRange<float>(50f, 250f)));
        DotSize = cfg.Bind("Laser", "DotSize", 175f, new ConfigDescription("Laser dot size, percent.", new AcceptableValueRange<float>(100f, 200f)));
        ColorChoice = cfg.Bind("Laser", "LineColor", 3, new ConfigDescription("0 game, 1 green, 2 red, 3 yellow, 4 blue.", new AcceptableValueRange<int>(0, 4)));
        fNightVision = AccessTools.Field(AccessTools.TypeByName("OperatorTPS.NightVision"), "On");
        fRoot = AccessTools.Field(AccessTools.TypeByName("OperatorTPS.Game"), "Root");
        _weaponType = typeof(WeaponV3);
        if (_weaponType != null)
        {
            _pEquipped = AccessTools.Property(_weaponType, "isEquiped");
            _weaponIl2 = Il2CppType.From(_weaponType);
        }
    }

    internal static int Shown;
    internal static Vector3 MyEnd;
    internal static int MyEndFrame = -10;

    internal static void Tick()
    {
        Shown = 0;
        foreach (var b in _beams.Values) b.used = false;
        _hiddenNow.Clear();
        try
        {
            UpdateDust();
            if (On.Value) DrawAll();
            else
            {
                RestoreGameLights();
                if (_state != "off") { _state = "off"; Plugin.Out.LogInfo("[Laser] the Show beam setting is OFF, so no beam is drawn"); }
            }
        }
        catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        foreach (var kv in new List<KeyValuePair<IntPtr, LineRenderer>>(_hidden))
            if (!_hiddenNow.Contains(kv.Key))
            {
                try { if (kv.Value != null) kv.Value.forceRenderingOff = false; } catch { }
                _hidden.Remove(kv.Key);
            }
        foreach (var b in _beams.Values)
            if (!b.used && b.lr != null) { b.lr.enabled = false; if (b.dot != null) b.dot.enabled = false; }
    }

    static void RestoreGameLights()
    {
        foreach (var g in _gameLights.Values) try { if (g.l != null) g.l.color = g.c; } catch { }
        _gameLights.Clear();
    }

    static void DrawAll()
    {
        if (!Plugin.HasPlayer || Cursor.lockState != CursorLockMode.Locked) return;
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 0.5f;
            try { Scan(); } catch (Exception e) { Plugin.Out.LogDebug(e.Message); }
        }
        var tp = Plugin.TpCamera;
        var main = Camera.main;
        var cam = Plugin.ThirdPerson && tp != null && tp.isActiveAndEnabled ? tp : main != null && main.enabled ? main : tp;
        if (cam == null || _sources.Count == 0) { Tell(cam == null ? "no beam: no camera found" : "no beam: no laser found on the weapon in your hands (is the laser switched on?)"); return; }

        bool nv = NightVision;
        var root = fRoot != null ? fRoot.GetValue(null) as Transform : Game.Pn()?.transform;
        Vector3 camPos = cam.transform.position;

        int drawn = 0; _why = "";
        foreach (var src in _sources)
        {
            try
            {
                var light = src.light;
                Color lightColor = Color.red;
                if (light != null)
                {
                    if (!_gameLights.TryGetValue(light.Pointer, out var gl)) _gameLights[light.Pointer] = gl = (light, light.color);
                    lightColor = gl.c;
                    if (light.color != Color.black) light.color = Color.black;
                }
                var line = src.part != null ? src.part.lineRenderer : null;
                bool lightOn = light != null && light.enabled && light.gameObject.activeInHierarchy && light.intensity > 0f;
                bool lineOn = line != null && line.enabled && line.gameObject.activeInHierarchy;
                if (light != null)
                {
                    if (!nv) _onBeforeNv[light.Pointer] = lightOn;
                    else if (!lightOn && light.enabled && light.intensity > 0f && _onBeforeNv.TryGetValue(light.Pointer, out var was) && was) lightOn = true;
                }
                if (!lightOn && !lineOn) { _why = "laser is off (light " + (light != null) + ", line " + (line != null) + ")"; continue; }
                if (line != null)
                {
                    line.forceRenderingOff = true;
                    _hidden[line.Pointer] = line;
                    _hiddenNow.Add(line.Pointer);
                }
                if (light != null && light.type == LightType.Spot && light.spotAngle > MaxSpotAngle && line == null) { _why = "wide light, not a laser"; continue; }

                var t = src.emitter;
                if (t == null || (!t.gameObject.activeInHierarchy && !(nv && lightOn)) || !InHands(t)) { _why = "emitter hidden or weapon not in hands"; continue; }
                if (src.ir && !nv) { _why = "infrared laser, night vision off"; continue; }
                if (!Plugin.ThirdPerson && Plugin.Ads && root != null && t.IsChildOf(root)) { _why = "first-person aiming"; continue; }

                Vector3 start = t.position + t.forward * LensOffset;
                if ((start - camPos).sqrMagnitude > MaxDistance * MaxDistance) { _why = "too far"; continue; }
                float range = src.range > 1f ? Mathf.Min(src.range, MaxDistance) : MaxDistance;
                Vector3 end = HitPoint(start, t.forward, range, root);
                bool hit = (end - (start + t.forward * range)).sqrMagnitude > 0.0001f;

                Color c = src.ir || light == null ? new Color(0.86f, 1f, 1f) : ChosenColor(Normalize(lightColor));
                SetBeam(t.Pointer, start, end, c, t.gameObject.layer, hit, nv);
                if (hit && root != null && t.IsChildOf(root)) { MyEnd = end; MyEndFrame = Time.frameCount; }
                drawn++;
            }
            catch (Exception e) { _why = "error: " + e.Message; Plugin.Out.LogDebug(e.Message); }
        }
        Shown = drawn;
        Tell(drawn > 0 ? "drawing " + drawn + " beam(s), show beam " + On.Value + ", night vision " + nv : "no beam: " + _why);
    }

    static void Tell(string state)
    {
        if (state == _state || Time.unscaledTime < _nextState) return;
        _state = state; _nextState = Time.unscaledTime + 1f;
        Plugin.Out.LogInfo("[Laser] " + state + " (" + _sources.Count + " source(s))");
    }

    static string _why = "", _state = ""; static float _nextState;

    static Color ChosenColor(Color game) => ColorChoice.Value switch
    {
        1 => new Color(0.2f, 1f, 0.15f),
        2 => new Color(1f, 0.12f, 0.08f),
        3 => new Color(1f, 0.85f, 0.1f),
        4 => new Color(0.15f, 0.45f, 1f),
        _ => game
    };

    static Color Normalize(Color c)
    {
        float m = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
        return m > 0.01f ? new Color(c.r / m, c.g / m, c.b / m) : Color.red;
    }

    static void SetBeam(IntPtr key, Vector3 a, Vector3 b, Color col, int layer, bool hit, bool nv)
    {
        if (!_beams.TryGetValue(key, out var w) || w.go == null || w.lr == null)
        {
            w = CreateBeam();
            if (w == null) return;
            _beams[key] = w;
        }

        Vector3 dir = (b - a).normalized;
        float len = Vector3.Distance(a, b), fade = nv ? NightVisionFadeDistance : FadeDistance;
        Vector3 e = len > fade ? a + dir * fade : b;
        var st = new Vector4(Mathf.Min(len, fade) / fade, 1f, 0f, 0f);
        foreach (var n in new[] { "_BaseColorMap_ST", "_EmissiveColorMap_ST" })
            try { if (w.mat.HasProperty(n)) w.mat.SetVector(n, st); } catch { }
        float dt = Mathf.Max(0.001f, Time.unscaledDeltaTime);
        float target = w.lastDir == Vector3.zero ? 0f : Mathf.Clamp01((Vector3.Angle(w.lastDir, dir) / dt - 15f) / 60f);
        w.lastDir = dir;
        w.swing = Mathf.Lerp(w.swing, target, 1f - Mathf.Exp(-dt * (target > w.swing ? 6f : 2f)));

        float pale = Paleness.Value / 100f, strength = Strength.Value / 100f;
        var pc = new Color(Mathf.Lerp(col.r, 1f, pale), Mathf.Lerp(col.g, 1f, pale), Mathf.Lerp(col.b, 1f, pale), Mathf.Clamp01(strength));
        w.lr.startColor = w.lr.endColor = pc;
        float self = (nv ? NightVisionBeamGlow : DayBeamGlow) * strength;
        w.mat.SetColor("_EmissiveColor", new Color(pc.r * self, pc.g * self, pc.b * self, 1f));
        var tint = new Color(pc.r, pc.g, pc.b, Mathf.Lerp(1f, 1f - Shimmer.Value / 100f, w.swing) * pc.a * (nv ? 0.5f : 1f));
        foreach (var n in BaseColors) if (w.mat.HasProperty(n)) w.mat.SetColor(n, tint);

        var rc = Plugin.ThirdPerson && Plugin.TpCamera != null ? Plugin.TpCamera : Camera.main;
        float px = rc == null ? 0f : 2f * Mathf.Tan(rc.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(rc.pixelHeight, Screen.height);
        float thick = Thickness.Value / 100f * (nv ? 0.75f : 1f);
        Vector3 eye = rc == null ? a : rc.transform.position;
        w.lr.widthMultiplier = 1f;
        w.lr.startWidth = rc == null ? FallbackWidth : Vector3.Distance(eye, a) * px * thick;
        w.lr.endWidth = rc == null ? FallbackWidth : Vector3.Distance(eye, e) * px * thick;

        w.dot.enabled = hit && rc != null && (nv || len <= DaylightDotDistance);
        if (w.dot.enabled)
        {
            float size = Vector3.Distance(eye, b) * px * DotPixels * DotSize.Value / 100f;
            Vector3 toCam = (eye - b).normalized;
            Vector3 p = b + toCam * Mathf.Min(0.02f, size);
            float dg = nv ? NightVisionDotGlow : DotGlow;
            w.dot.gameObject.layer = layer;
            w.dot.startWidth = w.dot.endWidth = size;
            w.dotMat.SetColor("_EmissiveColor", new Color(col.r * dg, col.g * dg, col.b * dg, 1f));
            w.dot.SetPosition(0, p - toCam * size * 0.01f);
            w.dot.SetPosition(1, p + toCam * size * 0.01f);
        }

        if (w.go.layer != layer) w.go.layer = layer;
        w.lr.SetPosition(0, a);
        w.lr.SetPosition(1, e);
        w.lr.enabled = true;
        w.used = true;
    }

    static Beam CreateBeam()
    {
        var bm = BaseMaterial();
        if (bm == null) return null;

        var w = new Beam { go = new GameObject("GhostReconView_Laser") { hideFlags = HideFlags.HideAndDontSave } };
        UnityEngine.Object.DontDestroyOnLoad(w.go);
        w.mat = new Material(bm) { hideFlags = HideFlags.HideAndDontSave, renderQueue = 2750 };
        foreach (var (n, v) in new (string, float)[] {
            ("_EmissiveExposureWeight", 0f), ("_DoubleSidedEnable", 1f), ("_CullMode", 0f), ("_CullModeForward", 0f), ("_DoubleSidedNormalMode", 1f),
            ("_MaterialID", 4f), ("_EnergyConservingSpecularColor", 0f), ("_SpecularAAScreenSpaceVariance", 0f),
            ("_ReceivesSSR", 0f), ("_ReceivesSSRTransparent", 0f), ("_Smoothness", 0f), ("_Metallic", 0f), ("_SpecularOcclusionMode", 0f), ("_CoatMask", 0f),
            ("_SurfaceType", 1f), ("_BlendMode", 1f), ("_SrcBlend", 1f), ("_DstBlend", 1f), ("_AlphaSrcBlend", 1f), ("_AlphaDstBlend", 1f),
            ("_ZWrite", 0f), ("_TransparentZWrite", 0f), ("_EnableBlendModePreserveSpecularLighting", 0f), ("_UseEmissiveIntensity", 0f) })
            if (w.mat.HasProperty(n)) w.mat.SetFloat(n, v);
        if (w.mat.HasProperty("_SpecularColor")) w.mat.SetColor("_SpecularColor", Color.black);
        foreach (var kw in new[] { "_SURFACE_TYPE_TRANSPARENT", "_BLENDMODE_ADD", "_EMISSIVE_COLOR_MAP", "_DOUBLESIDED_ON", "_MATERIAL_FEATURE_SPECULAR_COLOR", "_DISABLE_SSR", "_DISABLE_SSR_TRANSPARENT" })
            w.mat.EnableKeyword(kw);
        foreach (var kw in new[] { "_BLENDMODE_ALPHA", "_BLENDMODE_PRE_MULTIPLY", "_BLENDMODE_PRESERVE_SPECULAR_LIGHTING", "_EMISSIVE_MAPPING_BASE" })
            w.mat.DisableKeyword(kw);
        if (DustTexture() != null && w.mat.HasProperty("_BaseColorMap")) w.mat.SetTexture("_BaseColorMap", _dust);
        if (w.mat.HasProperty("_EmissiveColorMap")) w.mat.SetTexture("_EmissiveColorMap", _dust);

        w.lr = Line(w.go, w.mat, 0);
        w.lr.textureMode = LineTextureMode.Stretch;

        w.dotMat = new Material(w.mat) { hideFlags = HideFlags.HideAndDontSave };
        if (w.dotMat.HasProperty("_BaseColorMap")) w.dotMat.SetTexture("_BaseColorMap", null);
        if (w.dotMat.HasProperty("_EmissiveColorMap")) w.dotMat.SetTexture("_EmissiveColorMap", null);
        w.dotMat.DisableKeyword("_EMISSIVE_COLOR_MAP");
        foreach (var n in BaseColors) if (w.dotMat.HasProperty(n)) w.dotMat.SetColor(n, Color.black);
        var dotGo = new GameObject("GhostReconView_Dot") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(dotGo);
        w.dot = Line(dotGo, w.dotMat, 8);
        return w;
    }

    static LineRenderer Line(GameObject go, Material mat, int caps)
    {
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = mat;
        lr.useWorldSpace = true;
        lr.positionCount = 2;
        lr.numCapVertices = caps;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.alignment = LineAlignment.View;
        lr.generateLightingData = true;
        return lr;
    }

    static Material BaseMaterial()
    {
        if (_baseMat != null) return _baseMat;
        foreach (var s in _sources)
        {
            var m = s.part != null && s.part.lineRenderer != null ? s.part.lineRenderer.sharedMaterial : null;
            if (m != null && m.HasProperty("_EmissiveColor")) return _baseMat = m;
        }
        return null;
    }

    static Texture2D DustTexture()
    {
        if (_dust != null || _dustFailed) return _dust;
        try
        {
            _dust = new Texture2D(DustSize, 1, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var rnd = new System.Random(11);
            _dustBase = new float[DustSize];
            for (int i = 0; i < DustSize; i++)
            {
                float v = 0.3f + 0.5f * (float)rnd.NextDouble();
                if (rnd.NextDouble() < 0.2) v = 0f;
                if (rnd.NextDouble() < 0.05) v = 1f;
                _dustBase[i] = v;
            }
            UpdateDust();
        }
        catch { _dustFailed = true; _dust = null; }
        return _dust;
    }

    static void UpdateDust()
    {
        if (_dust == null) return;
        bool nv = NightVision;
        float g = Grain.Value / 100f;
        if (nv == _dustNv && g == _dustGrain) return;
        _dustNv = nv; _dustGrain = g;
        var px = new Color32[DustSize];
        for (int x = 0; x < DustSize; x++)
        {
            float t = x / (float)(DustSize - 1);
            float v = (nv ? 0.33f : Mathf.Lerp(Mathf.Max(0f, Mathf.LerpUnclamped(0.8f, _dustBase[x], g)), 0.8f, t)) * (1f - t) * (1f - t * 0.5f);
            byte b = (byte)(255f * Mathf.Clamp01(v / 0.8f));
            px[x] = new Color32(b, b, b, b);
        }
        _dust.SetPixels32(px);
        _dust.Apply(false);
    }

    static int _lastCount = -1;
    static void Scan()
    {
        _sources.Clear();
        int before = _lastCount;
        var seen = new HashSet<IntPtr>();
        foreach (var l in Track.Lasers)
        {
            bool live = false;
            try { live = l != null && l.gameObject.activeInHierarchy; } catch { }
            if (!live) continue;
            var lt = l._light;
            if (lt != null) seen.Add(lt.Pointer); else seen.Add(l.Pointer);
            _sources.Add(new Source { light = lt, part = l, emitter = lt != null ? lt.transform : l.transform, ir = l.IRonly, range = l.range });
        }

        foreach (var weapon in Track.Weapons)
        {
            bool held = false;
            try { held = weapon != null && weapon.gameObject.activeInHierarchy && weapon.isEquiped; } catch { }
            if (!held) continue;
            try
            {
                foreach (var l in weapon.GetComponentsInChildren<IRLaserLight>(false))
                {
                    if (l == null) continue;
                    var ll = l._light;
                    if (ll != null) { if (seen.Contains(ll.Pointer)) continue; seen.Add(ll.Pointer); }
                    else if (!seen.Add(l.Pointer)) continue;
                    _sources.Add(new Source { light = ll, part = l, emitter = ll != null ? ll.transform : l.transform, ir = l.IRonly, range = l.range });
                }
            }
            catch { }
            var lights = weapon.GetComponentsInChildren(Il2CppType.Of<Light>(), false);
            if (lights == null) continue;
            foreach (var lo in lights)
            {
                var lt = lo.TryCast<Light>();
                if (lt == null || seen.Contains(lt.Pointer) || lt.type != LightType.Spot || lt.spotAngle > MaxSpotAngle) continue;
                seen.Add(lt.Pointer);
                _sources.Add(new Source { light = lt, emitter = lt.transform });
            }
        }
        _lastCount = _sources.Count;
        if (_lastCount != before) Plugin.Out.LogInfo("[Laser] " + _lastCount + " laser source(s) found (" + Track.Lasers.Count + " tracked parts, " + Track.Weapons.Count + " weapons)");
    }

    static bool Equipped(Component weapon)
    {
        try { return _pEquipped == null || (bool)_pEquipped.GetValue(Activator.CreateInstance(_weaponType, weapon.Pointer)); }
        catch { return true; }
    }

    static bool InHands(Transform emitter)
    {
        if (_weaponIl2 == null) return true;
        if (!_weaponOf.TryGetValue(emitter.Pointer, out var weapon))
            _weaponOf[emitter.Pointer] = weapon = emitter.GetComponentInParent(_weaponIl2);
        return weapon == null || Equipped(weapon);
    }

    static Vector3 HitPoint(Vector3 origin, Vector3 dir, float max, Transform owner)
    {
        Vector3 from = origin;
        for (int i = 0; i < 6; i++)
        {
            float left = max - Vector3.Distance(origin, from);
            if (left <= 0.05f) break;
            if (!Game.Cast(from, dir, left, owner, out var p)) break;
            if (Vector3.Distance(origin, p) >= 0.6f) return p;
            from = p + dir * 0.03f;
        }
        return origin + dir * max;
    }
}
