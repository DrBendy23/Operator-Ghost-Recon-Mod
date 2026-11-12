using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GhostReconView;

// Two drones, one of each per mission:
//  - Recon (5-inch): reusable, marks enemies you hold the crosshair on for 1 s
//  - Attack (7-inch FPV): explodes on impact or on left click, damages AI in a radius
// W/S forward/back, A/D left/right, scroll wheel up/down, mouse to look, mouse 3 detonate, V view, Backspace recall.
// While you fly: your operator holds the game's own tablet (everyone sees that), gets no input, and the camera is the drone's.
internal static class Drone
{
    internal static ConfigEntry<KeyCode> ReconKey, AttackKey;
    internal static ConfigEntry<float> BatteryMin, Response;

    enum Kind { None, Recon, Attack }
    static Kind _kind;
    static bool _flying, _fpv, _reconUsedUp, _attackUsedUp, _exploded;
    static GameObject _root;
    static Camera _cam;
    static string _scene = "";
    static Transform[] _propT = new Transform[4];
    static Vector3 _pos, _vel, _home;
    static float _look, _altTarget;
    static float _yaw, _pitch, _roll, _throttle, _battery, _signal, _startAt, _markAt, _lastSync;
    static Health _markTarget;
    static IntPtr _spawnPtr;
    static Texture2D _px;
    static readonly System.Random _rng = new();

    internal static void Init(ConfigFile cfg)
    {
        ReconKey = cfg.Bind("Drone", "ReconKeyV4", KeyCode.LeftBracket, "Deploy / recall the recon drone.");
        AttackKey = cfg.Bind("Drone", "AttackKeyV4", KeyCode.RightBracket, "Deploy the attack drone.");
        Response = cfg.Bind("Drone", "Responsiveness", 100f, new ConfigDescription("How quickly the drone reacts and stops, percent. Lower = floatier, higher = snappier.", new AcceptableValueRange<float>(50f, 200f)));
        BatteryMin = cfg.Bind("Drone", "BatteryMinutesV2", 2f, new ConfigDescription("Flight time, minutes.", new AcceptableValueRange<float>(1f, 10f)));
    }

    const float RangeMeters = 1200f;   // signal range

    const float BreakSpeed = 4.2f;   // ≈ 15 km/h, same for both drones: light taps bounce, a real hit breaks it

    internal static bool Flying => _flying;

    internal static void InitPatches(HarmonyLib.Harmony h)
    {
        try
        {
            var mi = AccessTools.Method(typeof(InputLayer), "GetKey");
            if (mi != null) h.Patch(mi, prefix: new HarmonyLib.HarmonyMethod(typeof(Drone), nameof(GetKeyPrefix)));
            else Game.Log.LogWarning("[Drone] InputLayer.GetKey not found");
            foreach (var pn in new[] { "MouseMovement", "MouseMovementUnscaled" })
            {
                var g = AccessTools.PropertyGetter(typeof(InputLayer), pn);
                if (g != null) h.Patch(g, postfix: new HarmonyLib.HarmonyMethod(typeof(Drone), nameof(MousePostfix)));
            }
            var pf = AccessTools.Method(typeof(InputLayer.InputKeyBindInstance), "ProcessForThisFrame");
            if (pf != null) h.Patch(pf, postfix: new HarmonyLib.HarmonyMethod(typeof(Drone), nameof(BindPostfix)));
            else Game.Log.LogWarning("[Drone] InputKeyBindInstance.ProcessForThisFrame not found");
            var imp = AccessTools.Method(typeof(BulletInstance), "SpawnImpactNewSystem");
            if (imp != null) h.Patch(imp, prefix: new HarmonyLib.HarmonyMethod(typeof(Drone), nameof(BulletHitPrefix)));
            else Game.Log.LogWarning("[Drone] bullet impact hook not found");
            // the scroll wheel is the drone's while you fly (the game uses it to raise/lower your gun)
            foreach (var an in new[] { "GetAxis", "GetAxisRaw" })
            {
                var ax = AccessTools.Method(typeof(Input), an, new[] { typeof(string) });
                if (ax != null) { h.Patch(ax, postfix: new HarmonyLib.HarmonyMethod(typeof(Drone), nameof(AxisPostfix))); _axisPatched = true; }
            }
            var sd = AccessTools.PropertyGetter(typeof(Input), "mouseScrollDelta");
            if (sd != null) h.Patch(sd, postfix: new HarmonyLib.HarmonyMethod(typeof(Drone), nameof(ScrollDeltaPostfix)));
            // the game's camera: no mouse look on your operator while you fly
            foreach (var mn in new[] { "HandleCameraRotation", "RotateCamera" })
            {
                var cm = AccessTools.Method(typeof(CMF.CameraController), mn);
                if (cm != null) h.Patch(cm, prefix: new HarmonyLib.HarmonyMethod(typeof(Drone), nameof(FlyingSkipPrefix)));
            }
        }
        catch (Exception e) { Game.Log.LogWarning("[Drone] patch: " + e.Message); }
    }

    public static bool FlyingSkipPrefix() => !_flying;

    // A bullet (anyone's) hit the drone: recon breaks apart, attack explodes.
    static SphereCollider _hitBox;
    public static void BulletHitPrefix(RaycastHit hitInfo)
    {
        try
        {
            if (!_flying || _root == null || hitInfo.collider == null || !hitInfo.collider.transform.IsChildOf(_root.transform)) return;
            Game.Log.LogInfo("[Drone] shot down");
            if (_kind == Kind.Attack) Explode(); else { _vel *= 0.5f; Lose("crashed"); }
        }
        catch { }
    }
    static bool NvgKey(InputKey k) => k == InputKey.ToggleNVGs || k == InputKey.NVGGainIncrease || k == InputKey.NVGGainDecrease;

    static float _wheel; static bool _axisPatched;
    public static void AxisPostfix(string axisName, ref float __result)
    {
        if (!_flying || axisName != "Mouse ScrollWheel") return;
        if (Mathf.Abs(__result) > Mathf.Abs(_wheel)) _wheel = __result;
        __result = 0f;
    }
    public static void ScrollDeltaPostfix(ref Vector2 __result)
    {
        if (!_flying) return;
        if (Mathf.Abs(__result.y) > 0.001f && Mathf.Abs(_wheel) < 0.001f) _wheel = Mathf.Sign(__result.y) * 0.1f;
        __result = Vector2.zero;
    }

    // Runs right after the game updates one key's state for this frame: we overwrite that state.
    public static void BindPostfix(InputLayer.InputKeyBindInstance __instance)
    {
        try
        {
            var key = __instance.BoundKey;
            if ((_flying && !NvgKey(key)) || _ourActions.Contains(key)) { __instance.IsActive = false; __instance.WasActivatedThisFrame = false; }
        }
        catch { }
    }
    public static void MousePostfix(ref Vector2 __result) { if (_flying) __result = Vector2.zero; }

    // Game actions that share a key with the drone (e.g. drop weapon) are never triggered by a drone key press.
    static readonly HashSet<InputKey> _ourActions = new();
    static int _bindsLogged = -1;

    static bool _bindsValid;
    internal static bool BindsValid => _bindsValid;

    // Reads the game's key table (empty in the menus, so it retries) to find game actions sharing a drone key.
    static void ScanBinds()
    {
        try
        {
            var kb = InputLayer.Keybinds; if (kb == null) return;
            _ourActions.Clear();
            bool any = false;
            var sb = new System.Text.StringBuilder("[Drone] game key binds: ");
            foreach (var kv in kb)
            {
                var b = kv.Value; if (b == null) continue;
                var bind = b.KeyBind;
                if (bind.PrimaryKey != KeyCode.None) any = true;
                sb.Append(kv.Key).Append('=').Append(bind.PrimaryKey).Append('/').Append(bind.PressType).Append(' ');
                if (bind.PrimaryKey != KeyCode.None && (bind.PrimaryKey == ReconKey.Value || bind.PrimaryKey == AttackKey.Value || bind.PreliminaryKey == ReconKey.Value || bind.PreliminaryKey == AttackKey.Value))
                {
                    _ourActions.Add(kv.Key);
                    Game.Log.LogWarning("[Drone] game action " + kv.Key + " is on " + bind.PrimaryKey + " = a drone key; that game action is blocked");
                }
            }
            if (!any) { if (_bindsLogged < 0) { _bindsLogged = 0; Game.Log.LogInfo("[Drone] key table not loaded yet"); } return; }
            if (_bindsValid) return;
            _bindsValid = true;
            Game.Log.LogInfo(sb.ToString());
        }
        catch (Exception e) { Game.Log.LogDebug("[Drone] binds: " + e.Message); }
    }

    public static bool GetKeyPrefix(InputKey key, bool ActivatedThisFrame, ref bool __result)
    {
        if (_flying && !NvgKey(key)) { __result = false; return false; }      // while you fly, your operator gets no input at all (except NVG keys: the drone feed uses them)
        if (_ourActions.Contains(key)) { __result = false; return false; }      // a drone key never fires a game action
        return true;
    }

    static Tablet _tablet;

    // Exactly what the game does on M: held = Tablet.StartInteraction(), released = Tablet.ResumeInteraction().
    static void TabletPose(bool open)
    {
        try
        {
            if (open)
            {
                var pn = Pn(); _tablet = null;
                if (pn != null && pn.tablets != null)
                    for (int i = 0; i < pn.tablets.Count; i++) { var t = pn.tablets[i]; if (t != null) { _tablet = t; break; } }
                if (_tablet == null) { Game.Log.LogWarning("[Drone] no tablet on your operator"); return; }
                _tablet.StartInteraction();
                try { if (_tablet.animator != null && !string.IsNullOrEmpty(_tablet.Enable)) _tablet.animator.Play(_tablet.Enable); } catch (Exception e) { Game.Log.LogDebug("[Drone] tablet anim: " + e.Message); }
                Game.Log.LogInfo("[Drone] tablet out (StartInteraction + " + _tablet.Enable + ")");
            }
            else if (_tablet != null)
            {
                try { if (_tablet.animator != null && !string.IsNullOrEmpty(_tablet.Disable)) _tablet.animator.Play(_tablet.Disable); } catch { }
                _tablet.ResumeInteraction(); _tablet = null;
                Game.Log.LogInfo("[Drone] tablet away (ResumeInteraction)");
            }
        }
        catch (Exception e) { Game.Log.LogWarning("[Drone] tablet: " + e.Message); }
    }

    static PlayerNetworking Pn() { try { return PlayerMaster.MyPlayerMaster?.PlayerSpawnedObject; } catch { return null; } }

    internal static void Tick()
    {
        BoomCheck();
        // New spawn (new operation, or the same one restarted): both drones are ready again.
        try { var p0 = Pn(); var ptr = p0 != null ? p0.Pointer : IntPtr.Zero; if (ptr != _spawnPtr) { if (ptr != IntPtr.Zero) { _reconUsedUp = false; _attackUsedUp = false; ClearDebris(); } _spawnPtr = ptr; } } catch { }
        if (!_flying)
        {
            if (!_bindsValid && Time.frameCount % 60 == 0) ScanBinds();
            try { var sc0 = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; if (sc0 != _scene) { _scene = sc0; _reconUsedUp = false; _attackUsedUp = false; ClearDebris(); } } catch { }
            if (Cursor.lockState != CursorLockMode.Locked) return;
            if (Input.anyKeyDown && (Input.GetKeyDown(ReconKey.Value) || Input.GetKeyDown(AttackKey.Value))) Game.Log.LogInfo("[Drone] deploy key pressed");
            if (Input.GetKeyDown(ReconKey.Value)) Deploy(Kind.Recon);
            else if (Input.GetKeyDown(AttackKey.Value)) Deploy(Kind.Attack);
            return;
        }
        try
        {
            var sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (sc != _scene) { _scene = sc; _reconUsedUp = false; _attackUsedUp = false; ClearDebris(); End("level changed"); return; }
            if (Pn() == null) { End("player gone"); return; }
        }
        catch { }
        if (_root == null) { End("drone object gone"); return; }
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(ReconKey.Value) || Input.GetKeyDown(AttackKey.Value)) { Recall(); return; }
        try { Fly(); } catch (Exception e) { Game.Log.LogWarning("[Drone] " + e.Message); End("error"); }
    }

    // ---------- deploy / end ----------

    static void Deploy(Kind k)
    {
        try { DeployInner(k); } catch (Exception e) { Game.Log.LogWarning("[Drone] deploy failed: " + e.Message); End("deploy failed"); }
    }

    static void DeployInner(Kind k)
    {
        var pn = Pn(); if (pn == null) return;
        bool lobby = _scene.IndexOf("Operation Room", StringComparison.OrdinalIgnoreCase) >= 0 || _scene.IndexOf("Lobby", StringComparison.OrdinalIgnoreCase) >= 0 || _scene.IndexOf("Menu", StringComparison.OrdinalIgnoreCase) >= 0;
        if (lobby) { _reconUsedUp = false; _attackUsedUp = false; }   // unlimited drones in the lobby
        if (k == Kind.Recon && _reconUsedUp) { Notice("Recon drone lost"); return; }
        if (k == Kind.Attack && _attackUsedUp) { Notice("Attack drone used"); return; }
        var cc = Game.CamOf(pn);
        Vector3 fwd = Vector3.forward; try { fwd = cc != null ? Vector3.ProjectOnPlane(cc.GetAimingDirection(), Vector3.up).normalized : pn.transform.forward; } catch { }
        _kind = k; _exploded = false; _fpv = false;
        _home = pn.transform.position + Vector3.up * 1.2f + fwd * 1.0f;
        _pos = _home; _vel = Vector3.zero; _yaw = Quaternion.LookRotation(fwd).eulerAngles.y; _pitch = _roll = 0f;
        _battery = 1f; _signal = 1f; _startAt = Time.unscaledTime; _markTarget = null; _look = 0f; _altTarget = _pos.y;
        Build(pn, k);
        StartSound();
        SetupCamera();
        _flying = true;
        if (!_bindsValid) ScanBinds();
        SetPlayerInput(false);
        TabletPose(true);
        Game.Log.LogInfo("[Drone] " + k + " deployed");
    }

    static void End(string why)
    {
        bool was = _flying;
        _flying = false;
        SetPlayerInput(true);
        if (was) TabletPose(false);
        try { if (_cam != null && _fov0 > 0f) _cam.fieldOfView = _fov0; } catch { }
        try { if (_cam != null && _near0 > 0f) _cam.nearClipPlane = _near0; } catch { }
        _near0 = -1f;
        _audio = null; _hitBox = null; _seenSince.Clear();
        try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
        _root = null; _cam = null; _camReady = false;
        try { Pn()?.CMD_SetTabletJoyStickBlend(Vector2.zero); } catch { }
        try { HideAtakScreen(false); } catch { }
        Game.Log.LogInfo("[Drone] flight ended: " + why);
    }

    // No global input switch while flying: the game's own tablet code sits behind it and would never see the held tablet key.
    // Keys are forced in the key table (BindPostfix); mouse look is zeroed by MousePostfix + the camera patches.
    static void SetPlayerInput(bool on) { }

    static float _fov0 = -1f, _near0 = -1f; static Vector3 _camPos; static Quaternion _camRot; static float _camFov; static bool _camReady;

    // We never create a camera (that broke the game's renderer). We steer the game's own camera instead.
    static Camera GameCam() => Game.ViewCamera;

    static void SetupCamera()
    {
        _cam = GameCam();
        _camReady = false;
        try { if (_cam != null) _fov0 = _cam.fieldOfView; } catch { }
    }

    // Called right before the frame renders, after every game script has moved the camera.
    internal static void BeforeRender()
    {
        if (!_flying || !_camReady) return;
        try
        {
            var c = GameCam(); if (c == null) return;
            c.transform.SetPositionAndRotation(_camPos, _camRot);
            c.fieldOfView = _camFov;
        }
        catch { }
    }

    // ---------- flight ----------

    static void Fly()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        if (Input.GetKeyDown(KeyCode.Backspace)) { Recall(); return; }
        if (Input.GetKeyDown(KeyCode.V)) _fpv = !_fpv;
        if (_kind == Kind.Attack && (Input.GetMouseButtonDown(2) || Input.GetMouseButtonDown(3) || Input.GetMouseButtonDown(4))) { Explode(); return; }   // middle or either side button

        float s = 1f;
        float mx = Input.GetAxisRaw("Mouse X") * s, my = Input.GetAxisRaw("Mouse Y") * s;
        // Mouse = camera/heading (look around, the drone follows your heading). W/S forward/back, A/D left/right.
        // Scroll wheel = altitude (up to climb, down to descend). Mouse 3 = detonate. Right click = cycle vision.
        _yaw += mx * 2.5f;
        _look = Mathf.Clamp(_look - my * 1.6f, -60f, 60f);                                    // camera tilt only
        float fwdIn = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        float sideIn = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        // Real quad feel: keys tilt the frame, tilt pushes it, air drag slows it. Let go and it levels out and coasts to a stop.
        float tiltMax = _kind == Kind.Attack ? 42f : 34f, resp = Response.Value / 100f;
        _pitch = Mathf.MoveTowards(_pitch, fwdIn * tiltMax, tiltMax * 4.5f * resp * dt);   // ~0.22 s to full tilt at 100 %
        _roll = Mathf.MoveTowards(_roll, sideIn * tiltMax * 0.85f, tiltMax * 4.5f * resp * dt);
        // Scroll wheel: each notch moves the target height 0.35 m; the drone eases to it.
        float wheel = _axisPatched ? _wheel : Input.GetAxisRaw("Mouse ScrollWheel"); _wheel = 0f;
        if (Mathf.Abs(wheel) > 0.001f) _altTarget -= Mathf.Sign(wheel) * 0.35f;   // scroll toward you = up
        float climbIn = Mathf.Clamp((_altTarget - _pos.y) * 1.2f, -1f, 1f);
        if (Mathf.Abs(_altTarget - _pos.y) < 0.04f) climbIn = 0f;
        _throttle = Mathf.Clamp01(0.5f + climbIn * 0.4f);

        float maxUp = 3f, maxDown = 2.5f;
        var flat = Quaternion.Euler(0f, _yaw, 0f);
        var wind = new Vector3(Mathf.PerlinNoise(Time.time * 0.3f, 0f) - 0.5f, 0f, Mathf.PerlinNoise(0f, Time.time * 0.3f) - 0.5f) * 0.4f;
        // thrust from tilt (g·tan), drag against the air
        var push = flat * new Vector3(Mathf.Tan(_roll * Mathf.Deg2Rad), 0f, Mathf.Tan(_pitch * Mathf.Deg2Rad)) * 9.81f;
        var h = new Vector3(_vel.x, 0f, _vel.z);
        bool handsOff = fwdIn == 0f && sideIn == 0f;
        float k1 = (_kind == Kind.Attack ? 0.22f : 0.2f) * resp, k2 = (_kind == Kind.Attack ? 0.006f : 0.0072f) * resp;   // top speed ≈ 85 / 70 km/h
        if (handsOff) k1 += 0.55f * resp;                                                                                 // let go: it levels and brakes, with a short glide
        var drag = -h * (k1 + k2 * h.magnitude);
        h += (push + drag + wind) * dt;
        float vy = Mathf.MoveTowards(_vel.y, climbIn > 0f ? maxUp * climbIn : maxDown * climbIn, 4f * dt);
        _vel = new Vector3(h.x, vy, h.z);
        var rot = flat * Quaternion.Euler(_pitch, 0f, -_roll);
        var next = _pos + _vel * dt;

        UpdateSound(_vel.magnitude);
        AiHear();

        // Collision
        var step = next - _pos;
        bool blocked = false;
        RaycastHit hit = default;
        try { if (_hitBox != null) _hitBox.enabled = false; blocked = step.magnitude > 0.001f && Physics.SphereCast(_pos, 0.18f, step.normalized, out hit, step.magnitude + 0.05f, ~0, QueryTriggerInteraction.Ignore); }
        finally { if (_hitBox != null) _hitBox.enabled = true; }
        if (blocked)
        {
            float speed = _vel.magnitude;
            if (_kind == Kind.Attack)
            {
                bool person = false; try { person = hit.collider != null && hit.collider.GetComponentInParent<Health>() != null; } catch { }
                if (person || speed > BreakSpeed) { _pos = hit.point + hit.normal * 0.1f; Explode(); return; }   // hits someone, or a hard hit: the impact fuze goes
            }
            else if (speed > BreakSpeed) { _pos = hit.point + hit.normal * 0.2f; Lose("crashed"); return; }   // recon: comes apart
            if (speed > 1.2f) Bump(Mathf.Clamp01(speed / BreakSpeed));
            next = hit.point + hit.normal * 0.25f;
            _vel = Vector3.Reflect(_vel, hit.normal) * (speed > 4f ? 0.35f : 0.15f);
            _altTarget = next.y;                                                                   // blocked above/below: stop pushing
            if (speed > 4f) _battery -= 0.03f;                                                   // rough bump costs battery
            _pitch *= 0.3f; _roll *= 0.3f;
        }
        _pos = next;
        if (_root != null) _root.transform.SetPositionAndRotation(_pos, rot);
        float rpm = Mathf.Lerp(900f, 3600f, _throttle) * dt;
        for (int i = 0; i < 4; i++) if (_propT[i] != null) _propT[i].Rotate(0f, (i % 2 == 0 ? rpm : -rpm), 0f, Space.Self);

        // Battery and signal
        _battery -= dt / (BatteryMin.Value * 60f) * (0.85f + Mathf.Abs(climbIn) * 0.25f + Mathf.Abs(_pitch) / 40f * 0.2f);
        float dist = Vector3.Distance(_pos, _home);
        _signal = Mathf.Clamp01(1f - Mathf.Pow(dist / RangeMeters, 1.6f));
        if (_battery <= 0f) { if (_kind == Kind.Attack) { Explode(); return; } Lose("battery dead"); return; }
        if (_signal <= 0.02f) { if (_kind == Kind.Attack) { Explode(); return; } Lose("signal lost"); return; }

        // Operator works the tablet joystick for everyone to see
        if (Time.unscaledTime - _lastSync > 0.15f)
        {
            _lastSync = Time.unscaledTime;
            try { Pn()?.CMD_SetTabletJoyStickBlend(new Vector2(Mathf.Clamp(_roll / 35f, -1f, 1f), Mathf.Clamp(_pitch / 45f, -1f, 1f))); } catch { }
        }

        // Recon marking: hold the crosshair on an enemy for 1 s
        if (_kind == Kind.Recon) Mark();

        try { HideAtakScreen(true); } catch { }

        // Camera
        _cam = GameCam();
        if (_cam != null)
        {
            if (_fpv)
            {
                // FPV: lens on the top plate, wide, with the front props in the bottom corners like real FPV footage
                var lens = _kind == Kind.Attack ? new Vector3(0f, 0.11f, 0.09f) : new Vector3(0f, 0.045f, 0.02f);
                _camPos = _pos + rot * lens; _camRot = flat * Quaternion.Euler(_look + _pitch * 0.35f, 0f, 0f); _camFov = 115f;
            }
            else
            {
                var back = flat * Quaternion.Euler(_look * 0.6f, 0f, 0f) * Vector3.back;
                var want = _pos + back * 0.75f + Vector3.up * 0.22f;
                if (!_camReady) _camPos = want;
                _camPos = Vector3.Lerp(_camPos, want, 1f - Mathf.Exp(-12f * dt));
                _camRot = Quaternion.LookRotation(_pos + Vector3.up * 0.02f - _camPos, Vector3.up); _camFov = 60f;
            }
            _camReady = true;
            try { if (_near0 < 0f) _near0 = _cam.nearClipPlane; _cam.nearClipPlane = _fpv ? 0.03f : _near0; } catch { }
            _cam.transform.SetPositionAndRotation(_camPos, _camRot); _cam.fieldOfView = _camFov;
        }
    }

    // The game's tablet screen (ATAK) stays hidden on your display while you fly: you get the drone view, others get the tablet animation.
    static readonly List<Behaviour> _atakHidden = new();
    static float _nextAtak;
    static void HideAtakScreen(bool hide)
    {
        if (!hide)
        {
            foreach (var b in _atakHidden) { try { if (b != null) b.enabled = true; } catch { } }
            _atakHidden.Clear();
            return;
        }
        if (Time.unscaledTime < _nextAtak) return;
        _nextAtak = Time.unscaledTime + 0.25f;
        try
        {
            foreach (var a in UnityEngine.Object.FindObjectsOfType<AtakScreen>(true))
            {
                if (a == null) continue;
                foreach (var cv in a.GetComponentsInChildren<Canvas>(true)) if (cv != null && cv.enabled) { cv.enabled = false; _atakHidden.Add(cv); }
                foreach (var cv in a.GetComponentsInParent<Canvas>(true)) if (cv != null && cv.enabled) { cv.enabled = false; _atakHidden.Add(cv); }
            }
        }
        catch (Exception e) { Game.Log.LogDebug("[Drone] atak hide: " + e.Message); }
    }

    // Landing: the recon drone is only recovered if you bring it back within 3 m of you. Anywhere else it's left behind.
    static void Recall()
    {
        if (_kind == Kind.Recon)
        {
            float d = 99f; try { var pn = Pn(); if (pn != null) d = Vector3.Distance(Vector3.ProjectOnPlane(_pos - pn.transform.position, Vector3.up), Vector3.zero) + Mathf.Max(0f, Mathf.Abs(_pos.y - pn.transform.position.y) - 2f); } catch { }
            if (d > 3f)
            {
                _reconUsedUp = true;
                try   // it stays where you left it: drops to the ground, motors off
                {
                    var left = _root; _root = null;
                    if (_audio != null) { _audio.Stop(); _audio = null; }
                    if (_hitBox != null) { UnityEngine.Object.Destroy(_hitBox); _hitBox = null; }
                    var bc = left.AddComponent<BoxCollider>(); bc.size = new Vector3(0.3f, 0.06f, 0.3f) * _scale;
                    var rb = left.AddComponent<Rigidbody>(); rb.mass = 0.4f; rb.velocity = _vel * 0.3f;
                    _debris.Add(left);
                }
                catch { }
                Notice("Recon drone left behind (" + d.ToString("0") + " m away)"); End("left behind"); return;
            }
            Notice("Recon drone recovered");
        }
        else Notice("Attack drone recalled");
        End("recalled");
    }
    static void Lose(string why)
    {
        if (_kind == Kind.Recon) _reconUsedUp = true; else _attackUsedUp = true;
        if (why == "crashed") { try { Crash(); } catch (Exception e) { Game.Log.LogDebug("[Drone] crash: " + e.Message); } }
        Notice("Drone lost: " + why); End(why);
    }

    // ---------- crash: pieces fly off and stay on the ground, with a breaking sound ----------
    static Material _mCarbon, _mProp, _mBatt, _mMotor; static float _scale = 1f;
    static AudioClip _crashClip;
    static readonly List<GameObject> _debris = new();

    static void ClearDebris() { foreach (var g in _debris) { try { if (g != null) UnityEngine.Object.Destroy(g); } catch { } } _debris.Clear(); }

    // The drone's own parts come off: every arm, motor, prop, plate, battery and the camera become loose physical pieces.
    static void Crash()
    {
        var pos = _pos; var vel = _vel;
        int n = 0;
        if (_root != null)
        {
            var parts = new List<Transform>();
            foreach (var mr in _root.GetComponentsInChildren<MeshRenderer>(true)) if (mr != null) parts.Add(mr.transform);
            foreach (var t in parts)
            {
                try
                {
                    var mf = t.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue;
                    var b = mf.sharedMesh.bounds; var sz = Vector3.Scale(b.size, t.lossyScale);
                    if (sz.magnitude < 0.006f) { t.gameObject.SetActive(false); continue; }   // screws: lost in the grass
                    t.SetParent(null, true);
                    t.gameObject.hideFlags = HideFlags.HideAndDontSave;
                    var bc = t.gameObject.AddComponent<BoxCollider>(); bc.center = b.center; bc.size = new Vector3(Mathf.Max(b.size.x, 0.004f), Mathf.Max(b.size.y, 0.004f), Mathf.Max(b.size.z, 0.004f));
                    var rb = t.gameObject.AddComponent<Rigidbody>();
                    rb.mass = Mathf.Clamp(sz.x * sz.y * sz.z * 1500f, 0.005f, 0.25f); rb.drag = 0.4f; rb.angularDrag = 0.3f;
                    rb.velocity = vel * UnityEngine.Random.Range(0.2f, 0.6f) + UnityEngine.Random.insideUnitSphere * 3f + Vector3.up * UnityEngine.Random.Range(0.5f, 2.5f);
                    rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 30f;
                    _debris.Add(t.gameObject); n++;
                }
                catch { }
            }
        }
        // carbon shards on top of the parts
        for (int k = 0; k < 10 && _mCarbon != null; k++)
        {
            var sz = new Vector3(UnityEngine.Random.Range(0.015f, 0.05f), 0.003f, UnityEngine.Random.Range(0.01f, 0.03f));
            var go = Shape.Part("GRV_Debris_Shard", Shape.Box(sz), k % 3 == 0 ? _mProp : _mCarbon, null, Vector3.zero);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.SetPositionAndRotation(pos + UnityEngine.Random.insideUnitSphere * 0.1f, UnityEngine.Random.rotation);
            var bc = go.AddComponent<BoxCollider>(); bc.size = sz;
            var rb = go.AddComponent<Rigidbody>(); rb.mass = 0.01f; rb.drag = 0.6f;
            rb.velocity = vel * 0.3f + UnityEngine.Random.insideUnitSphere * 3.5f + Vector3.up * 1.5f;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 40f;
            _debris.Add(go); n++;
        }
        while (_debris.Count > 150) { try { if (_debris[0] != null) UnityEngine.Object.Destroy(_debris[0]); } catch { } _debris.RemoveAt(0); }
        try { AudioSource.PlayClipAtPoint(CrashClip(), pos, 1f); } catch (Exception e) { Game.Log.LogDebug("[Drone] crash sound: " + e.Message); }
        Game.Log.LogInfo("[Drone] crashed: " + n + " pieces on the ground");
    }

    // Wall bump: prop tips chattering on the wall + a hollow carbon/plastic knock.
    static AudioClip _bumpClip; static float _nextBump;
    static void Bump(float strength)
    {
        if (Time.unscaledTime < _nextBump) return; _nextBump = Time.unscaledTime + 0.18f;
        try
        {
            if (_bumpClip == null)
            {
                const int sr = 44100; int len = (int)(sr * 0.22f); var s = new float[len]; var rng = new System.Random(5);
                float lp = 0f;
                for (int i = 0; i < len; i++)
                {
                    float t = i / (float)sr, v = 0f;
                    // hollow knock: frame body resonance
                    v += (Mathf.Sin(2f * Mathf.PI * 310f * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * 760f * t) * 0.35f + Mathf.Sin(2f * Mathf.PI * 1870f * t) * 0.2f) * Mathf.Exp(-t * 38f);
                    // contact crack
                    float noise = (float)rng.NextDouble() * 2f - 1f; lp += (noise - lp) * 0.35f;
                    v += (noise - lp) * Mathf.Exp(-t * 120f) * 0.9f;
                    // prop tips chattering: 6 fast ticks over ~70 ms
                    for (int k = 0; k < 6; k++) { float c = 0.006f + k * 0.012f; float dt2 = t - c; if (dt2 > 0f && dt2 < 0.008f) v += ((float)rng.NextDouble() * 2f - 1f) * Mathf.Exp(-dt2 * 500f) * (0.55f - k * 0.07f); }
                    s[i] = Mathf.Clamp(v * 0.55f, -1f, 1f);
                }
                _bumpClip = AudioClip.Create("GRV_Bump", len, 1, sr, false);
                _bumpClip.SetData(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(s), 0);
            }
            AudioSource.PlayClipAtPoint(_bumpClip, _pos, 0.35f + strength * 0.65f);
        }
        catch { }
    }

    // Generated: a hard crack, then plastic and carbon bits clattering, fading over ~1.2 s.
    static AudioClip CrashClip()
    {
        if (_crashClip != null) return _crashClip;
        const int sr = 44100; int n = (int)(sr * 1.6f);
        var s = new float[n]; var rng = new System.Random(3);
        var ticks = new List<(float at, float amp, float tone)>();
        for (int k = 0; k < 10; k++) ticks.Add((0.004f + k * 0.009f, 0.7f, 0f));                                               // props shattering
        for (int k = 0; k < 26; k++) { float at = 0.12f + (float)Math.Pow(rng.NextDouble(), 1.6) * 1.35f; ticks.Add((at, 0.6f * (1f - at / 1.6f), 900f + (float)rng.NextDouble() * 2600f)); }   // pieces landing and skittering
        float lp = 0f;
        for (int i2 = 0; i2 < n; i2++)
        {
            float t = i2 / (float)sr, v = 0f;
            float noise = (float)rng.NextDouble() * 2f - 1f; lp += (noise - lp) * 0.25f;
            v += noise * Mathf.Exp(-t * 30f) * 0.9f;                                                                // the crack
            v += (Mathf.Sin(2f * Mathf.PI * 180f * t) * 0.6f + Mathf.Sin(2f * Mathf.PI * 470f * t) * 0.3f) * Mathf.Exp(-t * 18f);   // frame and battery thud
            foreach (var tk in ticks)
            {
                float dt2 = t - tk.at; if (dt2 <= 0f || dt2 > 0.03f) continue;
                float e = Mathf.Exp(-dt2 * 260f) * tk.amp;
                v += tk.tone > 0f ? (Mathf.Sin(2f * Mathf.PI * tk.tone * dt2) * 0.7f + (noise - lp) * 0.5f) * e : (noise - lp) * e;
            }
            s[i2] = Mathf.Clamp(v * 0.5f, -1f, 1f);
        }
        _crashClip = AudioClip.Create("GRV_Crash", n, 1, sr, false);
        _crashClip.SetData(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(s), 0);
        return _crashClip;
    }

    static void Mark()
    {
        if (_cam == null) { _markTarget = null; return; }
        Health best = null; float bestAng = 3f;
        foreach (var h in Marks.LiveEnemies())
        {
            Vector3 p; try { p = h.Chest != null ? h.Chest.position : h.transform.position + Vector3.up * 1.3f; } catch { continue; }
            float ang = Vector3.Angle(_cam.transform.forward, p - _cam.transform.position);
            if (ang < bestAng && Vector3.Distance(_cam.transform.position, p) < 400f) { best = h; bestAng = ang; }
        }
        if (best == null || best != _markTarget) { _markTarget = best; _markAt = Time.unscaledTime; return; }
        if (Time.unscaledTime - _markAt >= 1f && !Marks.MarkedUntil.ContainsKey(best.Pointer))
        {
            Marks.MarkedUntil[best.Pointer] = Time.unscaledTime + 90f;
            Game.Log.LogInfo("[Drone] enemy marked");
        }
    }

    const uint M67Id = 3298663573u;   // the game's thrown M67 (GrenadeExplosive + VFX + light + sound)
    static GrenadeExplosive _boom; static float _boomAt;

    static void Explode()
    {
        if (_exploded) return;
        _exploded = true; _attackUsedUp = true;
        bool done = false;
        try
        {
            if (Mirror.NetworkClient.prefabs.TryGetValue(M67Id, out var prefab) && prefab != null)
            {
                var go = UnityEngine.Object.Instantiate(prefab, _pos, Quaternion.identity);
                var ge = go.GetComponent<GrenadeExplosive>();
                if (ge != null)
                {
                    try { ge.minDelay = 0f; ge.maxDelay = 0f; ge.radius = 0.01f; ge.fragmentCount = 0; } catch { }   // the game's blast/fragments would hit anyone; we do the damage ourselves (enemies only)
                    try { if (ge.grenadeMesh != null) ge.grenadeMesh.SetActive(false); } catch { }
                    try { var rb = go.GetComponent<Rigidbody>(); if (rb != null) rb.isKinematic = true; } catch { }
                    if (Mirror.NetworkServer.active) { try { Mirror.NetworkServer.Spawn(go, (GameObject)null); } catch (Exception e) { Game.Log.LogDebug("[Drone] spawn: " + e.Message); } }
                    try { ge.Explode(); } catch (Exception e) { Game.Log.LogDebug("[Drone] Explode(): " + e.Message); }
                    _boom = ge; _boomAt = Time.unscaledTime;   // checked next frames: if the game's Explode() did nothing (not host), DoExplosion() runs the effect locally
                    done = true;
                }
                else UnityEngine.Object.Destroy(go);
            }
            else Game.Log.LogWarning("[Drone] M67 prefab not found");
        }
        catch (Exception e) { Game.Log.LogWarning("[Drone] explosion: " + e.Message); }
        // Warhead: kills AI enemies within 3 m, wounds to 5 m. Never you, never teammates (the drone knows who it belongs to).
        int killed = 0;
        try
        {
            int me = 0; try { me = PlayerMaster.MyPlayerMaster.UniqueID; } catch { }
            foreach (var h in Marks.LiveEnemies())
            {
                float dd = Vector3.Distance(h.transform.position + Vector3.up, _pos);
                if (dd > 5f) continue;
                float dmg = dd <= 3f ? 1000f : Mathf.Lerp(300f, 60f, (dd - 3f) / 2f);
                try { h.TakeDamage(dmg, default, default, me, _pos); if (dd <= 3f) killed++; } catch { }
            }
        }
        catch (Exception e) { Game.Log.LogDebug("[Drone] blast: " + e.Message); }
        Game.Log.LogInfo("[Drone] attack drone detonated (" + (done ? "game M67 explosion" : "no explosion prefab") + "), " + killed + " enemy kill(s) in the blast");
        End("detonated");
        Notice("Impact");
    }

    static void BoomCheck()
    {
        if (_boom == null) return;
        if (Time.unscaledTime - _boomAt < 0.25f) return;
        var ge = _boom; _boom = null;
        try
        {
            if (ge != null && !ge._exploded) { ge.DoExplosion(); Game.Log.LogInfo("[Drone] explosion played locally (DoExplosion)"); }
        }
        catch (Exception e) { Game.Log.LogDebug("[Drone] DoExplosion: " + e.Message); }
    }

    static float _noticeUntil; static string _notice = "";
    static void Notice(string s) { _notice = s; _noticeUntil = Time.unscaledTime + 2.5f; }

    // ---------- AI: enemies hear the drone, and those who can see it open fire on it ----------
    static float _nextHear; static BrainAI[] _brains; static float _nextBrains;
    static readonly Dictionary<IntPtr, float> _seenSince = new();

    static bool Sees(BrainAI b)
    {
        var from = b.transform.position + Vector3.up * 1.6f;
        try { if (_hitBox != null) _hitBox.enabled = false; return !Physics.Linecast(from, _pos, ~0, QueryTriggerInteraction.Ignore); }
        finally { if (_hitBox != null) _hitBox.enabled = true; }
    }

    static void AiHear()
    {
        if (Time.unscaledTime < _nextHear) return; _nextHear = Time.unscaledTime + 0.8f;
        try
        {
            if (_brains == null || Time.unscaledTime > _nextBrains) { _brains = UnityEngine.Object.FindObjectsOfType<BrainAI>(); _nextBrains = Time.unscaledTime + 3f; }
            float hearRange = _kind == Kind.Attack ? 22f : 16f;
            foreach (var b in _brains)
            {
                if (b == null) continue;
                bool dead = false; try { dead = b.health != null && b.health.isDead; } catch { }
                if (dead) continue;
                float dist = Vector3.Distance(b.transform.position, _pos);
                if (dist > 45f) { _seenSince.Remove(b.Pointer); continue; }
                if (dist < hearRange) { try { b.HeardTarget(_pos, dist, _root != null ? _root.transform : null); } catch { } }
                if (!Sees(b)) { _seenSince.Remove(b.Pointer); continue; }
                if (!_seenSince.ContainsKey(b.Pointer)) _seenSince[b.Pointer] = Time.unscaledTime;
                float seen = Time.unscaledTime - _seenSince[b.Pointer];
                try { b.LookAtTarget(_pos, false, false, false); } catch { }                        // eyes on it right away
                if (seen < 0.4f) continue;                                                     // a short beat, then they fire
                try
                {
                    // real fire: point their suppression at the drone (their bullets can hit it); don't interrupt a fight with a player
                    if (b.CurrentState != BrainAI.States.TargetSighted)
                    {
                        b._baseSuppressPosition = _pos; b.LastSuppressTargetPosition = _pos; b._lastSuppressTargetPosition = _pos;
                        b.LastTimeSuppressTargetChanged = Time.time; b._suppressDuration = 3f; b._suppressionIntelTimer = 0f;
                        if (b.CurrentState != BrainAI.States.Suppress) b.SetNewAiState(BrainAI.States.Suppress);
                    }
                }
                catch (Exception e) { Game.Log.LogDebug("[Drone] suppress: " + e.Message); }
                // after a while under fire a round finds it even if the spread misses
                if (seen > 1.6f && UnityEngine.Random.value < 0.3f) { Game.Log.LogInfo("[Drone] shot down by AI fire"); if (_kind == Kind.Attack) Explode(); else { _vel *= 0.5f; Lose("crashed"); } return; }
            }
        }
        catch (Exception e) { Game.Log.LogDebug("[Drone] hear: " + e.Message); }
    }

    // ---------- sound ----------
    // FPV motor whine, generated: four motors a few Hz apart (that beating buzz), harmonics for the saw edge, a little air noise.
    static AudioSource _audio; static AudioClip _clip;

    static AudioClip Clip()
    {
        if (_clip != null) return _clip;
        const int sr = 44100, n = sr;                       // exactly 1 s, integer frequencies → seamless loop
        var s = new float[n];
        float[] f = { 342f, 349f, 357f, 366f };
        var rng = new System.Random(7);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr, v = 0f;
            for (int m = 0; m < 4; m++)
            {
                float w = 2f * Mathf.PI * f[m] * t;
                v += 0.35f * Mathf.Sin(w) + 0.3f * Mathf.Sin(2f * w) + 0.22f * Mathf.Sin(3f * w) + 0.14f * Mathf.Sin(4f * w) + 0.09f * Mathf.Sin(5f * w) + 0.06f * Mathf.Sin(7f * w);
            }
            v += ((float)rng.NextDouble() - 0.5f) * 0.45f;   // air / prop wash
            s[i] = v * 0.08f;
        }
        _clip = AudioClip.Create("GRV_FPV", n, 1, sr, false);
        _clip.SetData(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(s), 0);
        return _clip;
    }

    static void StartSound()
    {
        try
        {
            if (_root == null) return;
            _audio = _root.AddComponent<AudioSource>();
            _audio.clip = Clip(); _audio.loop = true; _audio.playOnAwake = false;
            _audio.spatialBlend = 1f; _audio.minDistance = 1.5f; _audio.maxDistance = 90f; _audio.dopplerLevel = 0.4f; _audio.spread = 30f;
            _audio.volume = 0.7f; _audio.pitch = 0.9f;
            _audio.Play();
        }
        catch (Exception e) { Game.Log.LogWarning("[Drone] sound: " + e.Message); _audio = null; }
    }

    static void UpdateSound(float speed)
    {
        if (_audio == null) return;
        try
        {
            // revs up when you push forward, climb, or pick up speed; settles at hover
            float work = Mathf.Abs(_pitch) / 28f * 0.35f + Mathf.Abs(_throttle - 0.5f) * 1.2f + Mathf.Clamp01(speed / 25f) * 0.45f;
            float pitch = 0.9f + Mathf.Clamp01(work) * 0.75f;
            _audio.pitch = Mathf.Lerp(_audio.pitch, pitch, 0.18f);
            _audio.volume = (_fpv ? 0.4f : 0.65f) * (0.8f + Mathf.Clamp01(work) * 0.3f);
        }
        catch { }
    }

    // ---------- model ----------

    static Mesh Cyl(float r, float h, int seg, float r2 = -1f)
    {
        if (r2 < 0f) r2 = r;
        var v = new List<Vector3>(); var tri = new List<int>();
        for (int i = 0; i <= seg; i++)
        {
            float a = i / (float)seg * Mathf.PI * 2f;
            v.Add(new Vector3(Mathf.Cos(a) * r, -h / 2f, Mathf.Sin(a) * r));
            v.Add(new Vector3(Mathf.Cos(a) * r2, h / 2f, Mathf.Sin(a) * r2));
        }
        for (int i = 0; i < seg; i++) { int a = i * 2; tri.AddRange(new[] { a, a + 1, a + 2, a + 1, a + 3, a + 2 }); }
        int cb = v.Count; v.Add(new Vector3(0, -h / 2f, 0)); int ct = v.Count; v.Add(new Vector3(0, h / 2f, 0));
        for (int i = 0; i < seg; i++) { tri.AddRange(new[] { cb, (i + 1) * 2, i * 2, ct, i * 2 + 1, (i + 1) * 2 + 1 }); }
        return Shape.Finish(v, tri);
    }

    static void Build(PlayerNetworking pn, Kind k)
    {
        var src = Shape.BaseMat(pn);
        if (src == null) { Game.Log.LogWarning("[Drone] no base material"); return; }
        bool atk = k == Kind.Attack;
        float S = atk ? 1f : 0.72f;                                       // 7-inch vs 5-inch
        var carbon = Shape.Make(src, new Color(0.07f, 0.07f, 0.075f), 0.55f, 0.35f, Color.black);
        var motor = Shape.Make(src, atk ? new Color(0.12f, 0.12f, 0.13f) : new Color(0.55f, 0.57f, 0.6f), 0.75f, 0.9f, Color.black);
        var prop = Shape.Make(src, atk ? new Color(0.1f, 0.1f, 0.1f, 0.85f) : new Color(0.75f, 0.75f, 0.78f, 0.85f), 0.4f, 0.1f, Color.black);
        var batt = Shape.Make(src, new Color(0.1f, 0.12f, 0.25f), 0.35f, 0.1f, Color.black);
        var strap = Shape.Make(src, new Color(0.03f, 0.03f, 0.03f), 0.2f, 0f, Color.black);
        var lens = Shape.Make(src, new Color(0.02f, 0.03f, 0.06f), 0.98f, 0.9f, new Color(0.1f, 0.4f, 0.9f) * 0.5f);
        var led = Shape.Make(src, Color.red, 0.6f, 0f, new Color(2f, 0.1f, 0.1f) * 3f);
        var olive = Shape.Make(src, new Color(0.25f, 0.28f, 0.17f), 0.45f, 0.2f, Color.black);
        var body = atk ? carbon : Shape.Make(src, new Color(0.35f, 0.37f, 0.4f), 0.5f, 0.3f, Color.black);
        _mCarbon = carbon; _mProp = prop; _mBatt = batt; _mMotor = motor; _scale = S;

        _root = new GameObject("GRV_Drone") { hideFlags = HideFlags.HideAndDontSave };
        _hitBox = _root.AddComponent<SphereCollider>(); _hitBox.radius = 0.2f * S;   // bullets can hit it
        var r = _root.transform;
        // Frame: two X arms + top/bottom plates + standoffs
        float arm = 0.165f * S, armW = 0.016f * S, armT = 0.005f * S;
        for (int i = 0; i < 2; i++)
        {
            var go = new GameObject("Arm") { hideFlags = HideFlags.HideAndDontSave };
            go.transform.SetParent(r, false); go.transform.localRotation = Quaternion.Euler(0f, i == 0 ? 45f : -45f, 0f);
            Shape.Part("ArmMesh", Shape.Box(new Vector3(arm * 2f + 0.06f * S, armT, armW)), carbon, go.transform, Vector3.zero);
        }
        Shape.Part("PlateBottom", Shape.Box(new Vector3(0.085f * S, 0.003f, 0.11f * S)), carbon, r, new Vector3(0f, 0.004f, 0f));
        Shape.Part("PlateTop", Shape.Box(new Vector3(0.085f * S, 0.003f, 0.11f * S)), carbon, r, new Vector3(0f, 0.032f * S, 0f));
        foreach (var p in new[] { (-0.03f, -0.04f), (0.03f, -0.04f), (-0.03f, 0.04f), (0.03f, 0.04f) })
            Shape.Part("Standoff", Cyl(0.0035f, 0.028f * S, 8), motor, r, new Vector3(p.Item1 * S, 0.018f * S, p.Item2 * S));
        // Flight stack between plates
        Shape.Part("FC", Shape.Box(new Vector3(0.036f * S, 0.012f * S, 0.036f * S)), new Material(body) { color = new Color(0.05f, 0.2f, 0.08f) }, r, new Vector3(0f, 0.016f * S, 0f));
        // Motors + props + LEDs at the arm ends
        for (int i = 0; i < 4; i++)
        {
            float a = (45f + 90f * i) * Mathf.Deg2Rad;
            var p = new Vector3(Mathf.Sin(a) * arm, 0f, Mathf.Cos(a) * arm);
            Shape.Part("MotorBell", Cyl(0.014f * S, 0.018f * S, 16), motor, r, p + new Vector3(0f, 0.014f * S, 0f));
            Shape.Part("MotorBase", Cyl(0.016f * S, 0.004f, 16), carbon, r, p + new Vector3(0f, 0.004f, 0f));
            Shape.Part("Shaft", Cyl(0.0025f, 0.012f * S, 8), motor, r, p + new Vector3(0f, 0.028f * S, 0f));
            var hub = new GameObject("Prop") { hideFlags = HideFlags.HideAndDontSave };
            hub.transform.SetParent(r, false); hub.transform.localPosition = p + new Vector3(0f, 0.031f * S, 0f);
            hub.transform.localRotation = Quaternion.Euler(0f, (float)_rng.NextDouble() * 120f, 0f);
            Shape.Part("Hub", Cyl(0.006f * S, 0.006f, 10), prop, hub.transform, Vector3.zero);
            for (int b = 0; b < 3; b++)
            {
                var blade = new GameObject("Blade") { hideFlags = HideFlags.HideAndDontSave };
                blade.transform.SetParent(hub.transform, false);
                blade.transform.localRotation = Quaternion.Euler(0f, 120f * b, 0f) * Quaternion.Euler(0f, 0f, 9f);
                Shape.Part("BladeMesh", Shape.Box(new Vector3(0.088f * S, 0.0012f, 0.013f * S)), prop, blade.transform, new Vector3(0.046f * S, 0f, 0f));
            }
            _propT[i] = hub.transform;
            if (i >= 2) Shape.Part("LED", Shape.Box(new Vector3(0.012f * S, 0.004f, 0.006f * S)), led, r, p + new Vector3(0f, -0.001f, -0.012f * S));
        }
        // Motor wires (3 per arm) + ESC blocks + screws
        for (int i = 0; i < 4; i++)
        {
            float a = (45f + 90f * i) * Mathf.Deg2Rad; var dir = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            var esc = new GameObject("ESC") { hideFlags = HideFlags.HideAndDontSave }; esc.transform.SetParent(r, false);
            esc.transform.localPosition = dir * (arm * 0.55f) + new Vector3(0f, 0.009f * S, 0f); esc.transform.localRotation = Quaternion.LookRotation(dir);
            Shape.Part("EscBody", Shape.Box(new Vector3(0.012f * S, 0.005f, 0.028f * S)), new Material(body) { color = new Color(0.08f, 0.08f, 0.09f) }, esc.transform, Vector3.zero);
            for (int w = -1; w <= 1; w++)
            {
                var wire = new GameObject("Wire") { hideFlags = HideFlags.HideAndDontSave }; wire.transform.SetParent(r, false);
                wire.transform.localPosition = dir * (arm * 0.78f) + new Vector3(0f, 0.007f * S, 0f) + Quaternion.LookRotation(dir) * new Vector3(w * 0.004f * S, 0f, 0f);
                wire.transform.localRotation = Quaternion.LookRotation(dir) * Quaternion.Euler(90f, 0f, 0f);
                Shape.Part("WireMesh", Cyl(0.0012f, arm * 0.42f, 6), w == 0 ? led : strap, wire.transform, Vector3.zero);
            }
            foreach (var sx in new[] { -0.009f, 0.009f })
                Shape.Part("Screw", Cyl(0.002f, 0.0025f, 8), motor, r, dir * (arm - 0.006f * S) + new Vector3(sx * S, 0.007f, 0f));
        }
        // VTX + receiver boards on the stack, XT60 at the back
        Shape.Part("VTX", Shape.Box(new Vector3(0.03f * S, 0.006f * S, 0.03f * S)), new Material(body) { color = new Color(0.1f, 0.1f, 0.12f) }, r, new Vector3(0f, 0.026f * S, 0f));
        Shape.Part("XT60", Shape.Box(new Vector3(0.016f * S, 0.008f * S, 0.008f * S)), new Material(body) { color = new Color(0.9f, 0.75f, 0.1f) }, r, new Vector3(0f, 0.04f * S, -0.06f * S));
        // Battery on top with strap
        Shape.Part("Battery", Shape.Box(new Vector3(0.042f * S, 0.034f * S, 0.075f * S)), batt, r, new Vector3(0f, 0.052f * S, -0.01f * S));
        Shape.Part("Strap", Shape.Box(new Vector3(0.05f * S, 0.04f * S, 0.018f * S)), strap, r, new Vector3(0f, 0.052f * S, -0.01f * S));
        Shape.Part("BattLead", Cyl(0.002f, 0.04f * S, 6), led, r, new Vector3(0.012f * S, 0.06f * S, 0.03f * S));
        Shape.Part("Buckle", Shape.Box(new Vector3(0.014f * S, 0.044f * S, 0.02f * S)), motor, r, new Vector3(0.028f * S, 0.052f * S, -0.01f * S));
        Shape.Part("BattLabel", Shape.Box(new Vector3(0.0425f * S, 0.02f * S, 0.03f * S)), new Material(body) { color = new Color(0.85f, 0.85f, 0.85f) }, r, new Vector3(0f, 0.052f * S, -0.012f * S));
        // Camera: tilted mount at the front with a lens
        var camMount = new GameObject("CamMount") { hideFlags = HideFlags.HideAndDontSave };
        camMount.transform.SetParent(r, false); camMount.transform.localPosition = new Vector3(0f, 0.02f * S, 0.06f * S); camMount.transform.localRotation = Quaternion.Euler(-25f, 0f, 0f);
        Shape.Part("CamBody", Shape.Box(new Vector3(0.022f * S, 0.022f * S, 0.024f * S)), body, camMount.transform, Vector3.zero);
        Shape.Part("LensRing", Cyl(0.0095f * S, 0.006f, 16), motor, camMount.transform, new Vector3(0f, 0f, 0.013f * S));
        Shape.Part("Lens", Cyl(0.007f * S, 0.008f, 14), lens, camMount.transform, new Vector3(0f, 0f, 0.015f * S));
        Shape.Part("CamSidePlateL", Shape.Box(new Vector3(0.003f, 0.028f * S, 0.03f * S)), carbon, camMount.transform, new Vector3(-0.013f * S, 0f, -0.004f * S));
        Shape.Part("CamSidePlateR", Shape.Box(new Vector3(0.003f, 0.028f * S, 0.03f * S)), carbon, camMount.transform, new Vector3(0.013f * S, 0f, -0.004f * S));
        if (atk)
        {
            // Payload slung under the frame: RPG-style warhead with a fuze, zip-tied
            Shape.Part("WarheadBody", Cyl(0.021f * S, 0.12f * S, 16), olive, r, new Vector3(0f, -0.032f * S, 0f));
            var cone = new GameObject("Cone") { hideFlags = HideFlags.HideAndDontSave };
            cone.transform.SetParent(r, false); cone.transform.localPosition = new Vector3(0f, -0.032f * S, 0.095f * S); cone.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Shape.Part("ConeMesh", Cyl(0.021f * S, 0.07f * S, 16, 0.004f), olive, cone.transform, Vector3.zero);
            Shape.Part("Fuze", Cyl(0.006f * S, 0.03f * S, 10), motor, cone.transform, new Vector3(0f, 0.045f * S, 0f));
            Shape.Part("Tie1", Shape.Box(new Vector3(0.05f * S, 0.05f * S, 0.006f)), strap, r, new Vector3(0f, -0.02f * S, 0.03f * S));
            Shape.Part("Tie2", Shape.Box(new Vector3(0.05f * S, 0.05f * S, 0.006f)), strap, r, new Vector3(0f, -0.02f * S, -0.03f * S));
            // Twin VTX antennas at the back
            foreach (var x in new[] { -0.02f, 0.02f })
            {
                var ant = new GameObject("Ant") { hideFlags = HideFlags.HideAndDontSave };
                ant.transform.SetParent(r, false); ant.transform.localPosition = new Vector3(x * S, 0.045f * S, -0.06f * S); ant.transform.localRotation = Quaternion.Euler(-35f, 0f, x < 0 ? 20f : -20f);
                Shape.Part("AntRod", Cyl(0.0025f, 0.11f * S, 6), strap, ant.transform, new Vector3(0f, 0.055f * S, 0f));
                Shape.Part("AntTip", Cyl(0.006f, 0.02f * S, 8), body, ant.transform, new Vector3(0f, 0.115f * S, 0f));
            }
        }
        else
        {
            // Recon: gimbal dome under the nose + GPS puck + single antenna
            Shape.Part("Gimbal", Cyl(0.016f * S, 0.03f * S, 16), body, r, new Vector3(0f, -0.014f * S, 0.045f * S));
            Shape.Part("GimbalLens", Cyl(0.006f * S, 0.006f, 12), lens, r, new Vector3(0f, -0.02f * S, 0.062f * S));
            Shape.Part("GPS", Cyl(0.014f * S, 0.006f, 16), body, r, new Vector3(0f, 0.075f * S, -0.03f * S));
            Shape.Part("GPSMast", Cyl(0.003f, 0.018f * S, 6), strap, r, new Vector3(0f, 0.064f * S, -0.03f * S));
            var ant = new GameObject("Ant") { hideFlags = HideFlags.HideAndDontSave };
            ant.transform.SetParent(r, false); ant.transform.localPosition = new Vector3(0f, 0.04f * S, -0.06f * S); ant.transform.localRotation = Quaternion.Euler(-40f, 0f, 0f);
            Shape.Part("AntRod", Cyl(0.0025f, 0.09f * S, 6), strap, ant.transform, new Vector3(0f, 0.045f * S, 0f));
        }
        try
        {
            foreach (var mr in _root.GetComponentsInChildren<MeshRenderer>(true))
            {
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;
            }
        }
        catch { }
        Game.Log.LogInfo("[Drone] model built (" + (atk ? "attack 7in" : "recon 5in") + ")");
    }

    // ---------- HUD ----------

    internal static void Draw()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (_px == null) { _px = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave }; _px.SetPixel(0, 0, Color.white); _px.Apply(); }
        float W = Screen.width, H = Screen.height;
        if (Time.unscaledTime < _noticeUntil)
        {
            GUI.color = Color.white; GUI.contentColor = Color.white;
            GUI.Label(new Rect(W / 2f - 150f, H * 0.75f, 300f, 30f), _notice);
        }
        if (!_flying || Cursor.lockState != CursorLockMode.Locked) { GUI.color = Color.white; return; }

        var col = Color.white;
        GUI.color = col; GUI.contentColor = col;
        float cx = W / 2f;
        // Compass strip
        for (int dgr = -60; dgr <= 60; dgr += 5)
        {
            float hd = Mathf.Repeat(_yaw + dgr, 360f);
            float x = cx + dgr * (W * 0.004f);
            bool major = Mathf.Abs(Mathf.DeltaAngle(hd, Mathf.Round(hd / 45f) * 45f)) < 2.5f;
            GUI.DrawTexture(new Rect(x, 24f, 1f, major ? 12f : 6f), _px);
            if (major) { string n = ((int)Mathf.Round(hd / 45f) % 8) switch { 0 => "N", 1 => "NE", 2 => "E", 3 => "SE", 4 => "S", 5 => "SW", 6 => "W", _ => "NW" }; GUI.Label(new Rect(x - 12, 36f, 40, 20), n); }
        }
        GUI.DrawTexture(new Rect(cx - 1f, 18f, 2f, 22f), _px);
        // Crosshair dot + range to what it is on
        GUI.DrawTexture(new Rect(cx - 2f, H / 2f - 2f, 4f, 4f), _px);
        if (_cam != null && Physics.Raycast(_cam.transform.position, _cam.transform.forward, out var hit, 3000f, ~0, QueryTriggerInteraction.Ignore))
            GUI.Label(new Rect(cx + 14f, H / 2f - 10f, 90f, 22f), Mathf.RoundToInt(hit.distance) + " m");
        // Bottom bar: battery, speed, altitude, signal, clock
        float by = H - 70f;
        GUI.DrawTexture(new Rect(cx - 190f, by, 44f, 18f), _px); GUI.color = new Color(0, 0, 0, 1); GUI.DrawTexture(new Rect(cx - 188f, by + 2f, 40f, 14f), _px);
        GUI.color = _battery < 0.2f ? new Color(1f, 0.3f, 0.2f) : col; GUI.DrawTexture(new Rect(cx - 187f, by + 3f, 38f * Mathf.Clamp01(_battery), 12f), _px);
        GUI.DrawTexture(new Rect(cx - 146f, by + 5f, 3f, 8f), _px);
        GUI.color = col;
        GUI.Label(new Rect(cx - 138f, by - 1f, 60f, 22f), Mathf.RoundToInt(_battery * 100f) + "%");
        float ground = 0f; if (Physics.Raycast(_pos, Vector3.down, out var gh, 2000f, ~0, QueryTriggerInteraction.Ignore)) ground = gh.distance;
        GUI.Label(new Rect(cx - 80f, by - 1f, 90f, 22f), Mathf.RoundToInt(_vel.magnitude * 3.6f) + " km/h");
        GUI.Label(new Rect(cx + 5f, by - 1f, 90f, 22f), "ALT " + Mathf.RoundToInt(ground) + " m");
        for (int i = 0; i < 4; i++) { GUI.color = _signal > (i + 0.5f) / 4f ? col : new Color(col.r, col.g, col.b, 0.25f); GUI.DrawTexture(new Rect(cx + 110f + i * 8f, by + 14f - 4f * i, 6f, 4f + 4f * i), _px); }
        GUI.color = col;
        var t = TimeSpan.FromSeconds(Time.unscaledTime - _startAt);
        GUI.Label(new Rect(cx - 90f, by + 22f, 260f, 20f), t.ToString(@"mm\:ss") + "    " + Mathf.RoundToInt(Vector3.Distance(_pos, _home)) + " m from you");
        GUI.Label(new Rect(16f, H - 28f, 700f, 20f), (_kind == Kind.Attack ? "ATTACK   mouse 3 detonate   " : "RECON   hold crosshair 1 s to mark   ") + "W/S fwd/back   A/D left/right   scroll up/down   mouse look   V view   Backspace recall");
        // Recon mark progress
        if (_kind == Kind.Recon && _markTarget != null)
        {
            float f = Mathf.Clamp01((Time.unscaledTime - _markAt) / 1f);
            GUI.DrawTexture(new Rect(cx - 30f, H / 2f + 20f, 60f * f, 3f), _px);
        }
        GUI.color = Color.white;
    }

    // Thermal first pass: bodies as hot blobs along the bones
    static void ThermalBodies()
    {
        if (_cam == null) return;
        foreach (var h in Marks.LiveEnemies())
        {
            try
            {
                Transform[] pts = { h.Head, h.Chest, h.Stomach, h.LArm, h.RArm, h.LLeg, h.RLeg };
                foreach (var tr in pts)
                {
                    if (tr == null) continue;
                    var v = _cam.WorldToViewportPoint(tr.position);
                    if (v.z <= 0.1f) continue;
                    float size = Mathf.Clamp(900f / v.z, 6f, 60f) * (tr == h.Head ? 0.8f : 1.2f);
                    GUI.color = new Color(1f, 1f, 0.95f, 0.85f);
                    GUI.DrawTexture(new Rect(v.x * Screen.width - size / 2f, (1f - v.y) * Screen.height - size / 2f, size, size), _px);
                }
            }
            catch { }
        }
    }
}

// Small mesh/material helpers for the drone model (materials are clones of one of the game's own HDRP materials).
internal static class Shape
{
    internal static Mesh Finish(List<Vector3> v, List<int> tri)
    {
        var m = new Mesh { hideFlags = HideFlags.HideAndDontSave };
        m.vertices = v.ToArray(); m.triangles = tri.ToArray();
        m.RecalculateNormals(); m.RecalculateBounds();
        return m;
    }

    internal static Mesh Box(Vector3 size)
    {
        var h = size / 2f; var v = new List<Vector3>(); var tri = new List<int>();
        Vector3[] n = { Vector3.up, Vector3.down, Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
        foreach (var nn in n)
        {
            var u = nn == Vector3.up || nn == Vector3.down ? Vector3.right : Vector3.Cross(Vector3.up, nn).normalized;
            var w = Vector3.Cross(nn, u);
            var c = Vector3.Scale(nn, h);
            int b = v.Count;
            v.Add(c - Vector3.Scale(u, h) - Vector3.Scale(w, h)); v.Add(c + Vector3.Scale(u, h) - Vector3.Scale(w, h));
            v.Add(c + Vector3.Scale(u, h) + Vector3.Scale(w, h)); v.Add(c - Vector3.Scale(u, h) + Vector3.Scale(w, h));
            tri.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
        }
        var m = Finish(v, tri);
        // fix winding per face so normals point out
        var tris = m.triangles; var verts = m.vertices;
        for (int i = 0; i < tris.Length; i += 3)
        {
            var fn = Vector3.Cross(verts[tris[i + 1]] - verts[tris[i]], verts[tris[i + 2]] - verts[tris[i]]);
            var centre = (verts[tris[i]] + verts[tris[i + 1]] + verts[tris[i + 2]]) / 3f;
            if (Vector3.Dot(fn, centre) < 0f) { int t = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = t; }
        }
        m.triangles = tris; m.RecalculateNormals();
        return m;
    }

    internal static GameObject Part(string name, Mesh mesh, Material mat, Transform parent, Vector3 localPos)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.HideAndDontSave };
        go.transform.SetParent(parent, false); go.transform.localPosition = localPos;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; mr.receiveShadows = true;
        return go;
    }

    internal static Material BaseMat(PlayerNetworking pn)
    {
        try
        {
            if (pn != null)
                foreach (var r in pn.GetComponentsInChildren<Renderer>(true))
                {
                    var m = r != null ? r.sharedMaterial : null;
                    if (m != null && m.HasProperty("_BaseColor") && m.HasProperty("_Smoothness") && m.HasProperty("_EmissiveColor")) return m;
                }
        }
        catch { }
        return null;
    }

    internal static Material Make(Material src, Color color, float smooth, float metal, Color emissive)
    {
        var m = new Material(src) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            m.SetColor("_BaseColor", color); m.color = color;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
            if (m.HasProperty("_BaseColorMap")) m.SetTexture("_BaseColorMap", null);
            if (m.HasProperty("_NormalMap")) m.SetTexture("_NormalMap", null);
            if (m.HasProperty("_MaskMap")) m.SetTexture("_MaskMap", null);
            if (m.HasProperty("_EmissiveColorMap")) m.SetTexture("_EmissiveColorMap", null);
            if (m.HasProperty("_UseEmissiveIntensity")) m.SetFloat("_UseEmissiveIntensity", 0f);
            if (m.HasProperty("_EmissiveColor")) m.SetColor("_EmissiveColor", emissive);
        }
        catch { }
        return m;
    }
}
