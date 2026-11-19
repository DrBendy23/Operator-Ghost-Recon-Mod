using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Il2CppTMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace GhostReconView;

internal static class ModsMenu
{
    enum Mode { Native, Mods, Odg }
    static readonly string[] PageName = { "", "MODS", "ODG" };

    sealed class Row
    {
        internal ConfigEntryBase Entry;
        internal TMP_Text Value;
        internal Slider Slider;
        internal float Step;
        internal Image Pill;
        internal RectTransform Knob;
        internal TMP_Dropdown Drop;
        internal int DropBase;
    }

    sealed class Part
    {
        internal string Name;
        internal Color Color;
        internal bool Active, Enabled;
    }

    sealed class Style
    {
        internal readonly List<Part> Parts = new List<Part>();
        internal bool Interactable = true, Ok;
    }

    static readonly string[] Allowed =
    {
        "Image", "RawImage", "TextMeshProUGUI", "TMP_SubMeshUI", "Button", "LayoutElement", "HorizontalLayoutGroup", "VerticalLayoutGroup",
        "ContentSizeFitter", "Mask", "RectMask2D", "ScrollRect", "Scrollbar", "Slider", "UIElementSound", "TMP_Dropdown", "Toggle", "DropdownItem"
    };

    internal static readonly Dictionary<string, string[]> Choices = new Dictionary<string, string[]>
    {
        { "AimDot", new[] { "Off", "When no laser", "Always" } },
        { "AimDotColor", new[] { "White", "Green", "Red", "Yellow", "Blue" } },
        { "LineColor", new[] { "Game", "Green", "Red", "Yellow", "Blue" } },
        { "ControlMode", new[] { "Custom", "ODG" } },
        { "FlipMode", new[] { "Triple tap", "Flip key", "Right click" } },
        { "EnemyReaction", new[] { "Slow", "Normal", "Fast" } },
        { "Preset", new[] { "Custom", "Realism", "Film", "Warm", "Cold", "Noir" } },
        { "MicStaysOn", new[] { "Off", "Radio key", "Area voice key" } }
    };

    static readonly Color SwitchOn = new Color(0.2f, 0.78f, 0.35f, 1f), SwitchOff = new Color(0.45f, 0.45f, 0.48f, 0.75f);
    static Sprite _round;

    internal static readonly Dictionary<string, string> Titles = new Dictionary<string, string>
    {
        { "Keys", "VIEW" }, { "Laser", "LASER" }, { "Movement", "MOVEMENT" }, { "Drone", "DRONE" },
        { "Handling", "RELOAD" }, { "Cinematic", "CINEMATIC LOOK" }, { "Voice", "MIC" }, { "Map", "MAP AND PLAYBOOK" }
    };

    internal static readonly HashSet<string> Advanced = new HashSet<string>
    {
        "Laser.LineThickness", "Laser.LineStrength", "Laser.LinePaleness", "Laser.LineShimmer", "Laser.LineGrain", "Laser.DotSize",
        "Cinematic.Exposure", "Cinematic.Contrast", "Cinematic.Saturation", "Cinematic.Warmth", "Cinematic.Vignette", "Cinematic.Grain", "Cinematic.Bloom",
        "Drone.ClimbStep", "Drone.Responsiveness", "Drone.EnemyAccuracyV3", "Drone.EnemySeeRange", "Drone.EnemyReaction",
        "Movement.AutoLeanReachV3", "Keys.RequireShift", "Keys.DoorwayHoldSecondsV2", "Keys.DoorwaySwitchSecondsV2", "Voice.MicIconV2",
        "Keys.SwitchKey",
        "Laser.AimDotSize", "Laser.AimDotColor", "Drone.BlastRadius", "Drone.BlastDamage", "Drone.BlastThroughWalls", "Drone.BlastHitsTeammates", "Drone.TurboKey", "Drone.FlipKey", "Drone.DetonateKey",
    };

    static bool IsAdvanced(ConfigEntryBase e) => Advanced.Contains(e.Section + "." + e.Key);

    internal static readonly List<string> Order = new List<string> { "Map", "Keys", "Laser", "Drone", "Movement", "Handling", "Voice", "Cinematic" };

    static GameObject _page, _native, _holder, _rowT, _headerT, _sliderT;
    static readonly GameObject[] _ours = new GameObject[3];
    static readonly List<Row>[] _rows = { null, new List<Row>(), new List<Row>() };
    static readonly TMP_Text[] _saveText = new TMP_Text[3];
    static readonly Dictionary<ConfigEntryBase, object> _snapshot = new Dictionary<ConfigEntryBase, object>();
    static readonly Transform[] _navOurs = new Transform[3];
    static Transform _navControl;
    static TMP_Text _title;
    static string _titleNative;
    static readonly Style _selected = new Style(), _plain = new Style();
    static Mode _mode;
    static Row _binding;
    static int _bindFrame;
    static bool _failed, _updating, _waitSaid;
    static float _next, _nextRefresh, _savedUntil;
    static int _savedPage;
    static string _stage = "";

    static void Say(string s) { Plugin.Out.LogInfo("[Menu] " + s); }

    internal static void Tick()
    {
        if (_failed) return;
        try
        {
            if (_page == null)
            {
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + 1f;
                TryBuild();
                return;
            }
            if (_mode != Mode.Native && Cursor.lockState == CursorLockMode.Locked) SetMode(Mode.Native);
            if (_mode == Mode.Native) return;
            if (_binding != null) Capture();
            if (_savedPage != 0 && Time.unscaledTime >= _savedUntil)
            {
                if (_saveText[_savedPage] != null) _saveText[_savedPage].text = "SAVE SETTINGS";
                _savedPage = 0;
            }
            if (Time.unscaledTime >= _nextRefresh) { _nextRefresh = Time.unscaledTime + 0.3f; Refresh(); }
        }
        catch (Exception e)
        {
            _failed = true;
            Plugin.Out.LogWarning("[Menu] stopped at '" + _stage + "': " + e.Message);
        }
    }

    static Transform Child(Transform parent, string name)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            if (c.name.Trim() == name) return c;
        }
        return null;
    }

    static Transform Find(Transform t, params string[] names)
    {
        foreach (var n in names) { if (t == null) return null; t = Child(t, n); }
        return t;
    }

    static string TypeName(Component c)
    {
        try { return c.GetIl2CppType().Name; } catch { return ""; }
    }

    static string Parts(GameObject go)
    {
        var s = "";
        foreach (var c in go.GetComponents<Component>()) if (c != null) s += TypeName(c) + " ";
        return s.Trim();
    }

    static void TryBuild()
    {
        _stage = "find menu";
        PlayMenu menu = null; GameObject controls = null, audio = null;
        var pages = new List<GameObject>();
        foreach (var m in Resources.FindObjectsOfTypeAll<PlayMenu>())
        {
            if (m == null || m.AllSubMenus == null || !m.gameObject.scene.IsValid()) continue;
            foreach (var p in m.AllSubMenus)
            {
                if (p == null || !p.name.StartsWith("Settings/")) continue;
                pages.Add(p);
                if (p.name == "Settings/Controls") controls = p;
                if (p.name == "Settings/Audio") audio = p;
            }
            if (controls != null) { menu = m; break; }
            pages.Clear();
        }
        if (menu == null || controls == null || audio == null) return;

        _stage = "find parts";
        var native = Child(controls.transform, "Controls");
        var content = Find(native, "Keybinds", "Viewport", "Content");
        bool hasRow = false, hasHeader = false;
        if (content != null)
            for (int i = 0; i < content.childCount; i++)
            {
                string n = content.GetChild(i).name.Trim();
                if (n == "Header") hasHeader = true;
                if (n.StartsWith("KeybindPrefab")) hasRow = true;
            }
        if (!hasRow || !hasHeader)
        {
            if (!_waitSaid) { _waitSaid = true; Say("waiting for the controls list (list " + (content != null) + ", rows " + hasRow + ", titles " + hasHeader + ")"); }
            return;
        }

        OdgBridge.Load(Plugin.Config);

        _stage = "holder";
        _holder = new GameObject("GRV menu parts");
        _holder.SetActive(false);
        _holder.transform.SetParent(controls.transform, false);

        _stage = "slider template";
        Slider source = null;
        foreach (var s in audio.GetComponentsInChildren<Slider>(true)) { if (s != null && s.gameObject.activeSelf) { source = s; break; } }
        if (source == null) foreach (var s in controls.GetComponentsInChildren<Slider>(true)) { if (s != null) { source = s; break; } }
        if (source != null)
        {
            _sliderT = UnityEngine.Object.Instantiate(source.gameObject, _holder.transform);
            _sliderT.name = "GRV slider";
            Strip(_sliderT);
            _sliderT.SetActive(true);
            foreach (var cg in _sliderT.GetComponentsInChildren<CanvasGroup>(true)) { if (cg != null) { cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true; } }
            var trigger = Child(_sliderT.transform, "Raycast Trigger")?.TryCast<RectTransform>();
            if (trigger != null) trigger.sizeDelta = new Vector2(0f, -40f);
        }
        Say("slider template " + (_sliderT != null ? "ready" : "missing, numbers will use buttons"));

        for (int k = 1; k <= 2; k++)
        {
            _stage = "clone page " + k;
            var clone = UnityEngine.Object.Instantiate(native.gameObject, _holder.transform);
            clone.name = "GRV " + PageName[k];
            Prepare(clone, k);
            _stage = "fill page " + k;
            Fill(clone, k);
            clone.SetActive(false);
            clone.transform.SetParent(controls.transform, false);
            clone.transform.SetSiblingIndex(native.GetSiblingIndex() + k);
            _ours[k] = clone;
            Say(PageName[k] + " page built, " + _rows[k].Count + " settings");
        }

        _stage = "side buttons";
        foreach (var p in pages) AddNav(p, p == controls, p == audio);

        var title = Child(controls.transform, "Text (TMP)");
        if (title != null) { _title = title.GetComponent<TMP_Text>(); if (_title != null) _titleNative = _title.text; }

        _page = controls; _native = native.gameObject; _mode = Mode.Native;
        Say("ready: MODS and ODG added under AUDIO");
    }

    static void Strip(GameObject root)
    {
        foreach (var mb in root.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            if (Array.IndexOf(Allowed, TypeName(mb)) >= 0) continue;
            try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
        }
        foreach (var an in root.GetComponentsInChildren<Animator>(true)) { try { if (an != null) UnityEngine.Object.DestroyImmediate(an); } catch { } }
    }

    static TMP_Text SetText(Transform t, string text)
    {
        if (t == null) return null;
        var tmp = t.GetComponent<TMP_Text>();
        if (tmp != null) tmp.text = text;
        return tmp;
    }

    static void Shrink(TMP_Text tmp)
    {
        if (tmp == null) return;
        try
        {
            float size = tmp.fontSize;
            tmp.enableAutoSizing = true;
            tmp.fontSizeMax = size;
            tmp.fontSizeMin = Mathf.Min(size, 9f);
        }
        catch { }
    }

    static Button Clickable(Transform t, Action onClick)
    {
        var b = t.GetComponent<Button>();
        if (b == null) b = t.gameObject.AddComponent<Button>();
        b.onClick = new Button.ButtonClickedEvent();
        b.onClick.AddListener((UnityAction)onClick);
        b.interactable = true;
        return b;
    }

    static void Prepare(GameObject clone, int k)
    {
        Strip(clone);
        var content = Find(clone.transform, "Keybinds", "Viewport", "Content");
        var kill = new List<GameObject>();
        for (int i = 0; i < content.childCount; i++)
        {
            var c = content.GetChild(i);
            string n = c.name.Trim();
            if (n == "Keybind Reference") continue;
            if (k == 1 && _headerT == null && n == "Header") { _headerT = c.gameObject; continue; }
            if (k == 1 && _rowT == null && n.StartsWith("KeybindPrefab") && c.gameObject.activeSelf && Child(c, "Keybind Button") != null) { _rowT = c.gameObject; continue; }
            kill.Add(c.gameObject);
        }
        if (k == 1)
        {
            if (_headerT == null || _rowT == null) throw new Exception("row templates not found");
            _headerT.transform.SetParent(_holder.transform, false);
            _rowT.transform.SetParent(_holder.transform, false);
            _headerT.SetActive(true);
            var drop = Child(_rowT.transform, "Activation Dropdown");
            if (drop != null) drop.gameObject.SetActive(false);
            for (int i = 0; i < _rowT.transform.childCount; i++) { var c = _rowT.transform.GetChild(i); if (c.name.Trim() == "Background Conflict") c.gameObject.SetActive(false); }
            try { _round = Child(_rowT.transform, "Keybind Button").GetComponent<Image>().sprite; } catch { }
            Say("row parts: " + Parts(_rowT) + " | key cell: " + Parts(Child(_rowT.transform, "Keybind Button").gameObject));
        }
        foreach (var g in kill) UnityEngine.Object.DestroyImmediate(g);

        try
        {
            var lg = content.GetComponent<VerticalLayoutGroup>();
            if (lg != null) { var pad = lg.padding; pad.bottom = 24; lg.padding = pad; }
        }
        catch { }

        var head = Child(content, "Keybind Reference");
        if (head != null)
        {
            SetText(Child(head, "action label text"), "SETTING");
            SetText(Child(head, "key label text"), "VALUE");
            SetText(Child(head, "activation type text"), "RESET");
        }
        var input = Child(clone.transform, "Input"); if (input != null) input.gameObject.SetActive(false);
        var inputLabel = Child(clone.transform, "input label textr"); if (inputLabel != null) inputLabel.gameObject.SetActive(false);
        SetText(Child(clone.transform, "keybinds label text"), PageName[k]);

        int which = k;
        var undo = Child(clone.transform, "Undo Changes");
        if (undo != null) { SetText(Child(undo, "Text (TMP)"), "UNDO CHANGES"); Clickable(undo, () => Undo(which)); }
        var reset = Child(clone.transform, "Revert All Keybinds");
        if (reset != null) { Shrink(SetText(Child(reset, "Text (TMP)"), "RESET TO DEFAULT")); Clickable(reset, () => ResetAll(which)); }
        var save = Child(clone.transform, "Save Setings");
        if (save != null) { _saveText[k] = SetText(Child(save, "Text (TMP)"), "SAVE SETTINGS"); Clickable(save, () => Save(which)); }
    }

    static int Rank(string section)
    {
        int i = Order.IndexOf(section);
        return i < 0 ? Order.Count : i;
    }

    static void Fill(GameObject clone, int k)
    {
        var content = Find(clone.transform, "Keybinds", "Viewport", "Content");
        var list = new List<ConfigEntryBase>();
        foreach (var e in Plugin.Config.Entries)
        {
            bool odg = e is PrefKey || e is PrefBool;
            if (k == 2 ? odg : !odg) list.Add(e);
        }
        if (k == 1)
        {
            var sorted = new List<ConfigEntryBase>();
            var seen = new List<string>();
            foreach (var e in list) if (!seen.Contains(e.Section)) seen.Add(e.Section);
            seen.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
            foreach (var s in seen)
            {
                foreach (var e in list) if (e.Section == s && !IsAdvanced(e)) sorted.Add(e);
                foreach (var e in list) if (e.Section == s && IsAdvanced(e)) sorted.Add(e);
            }
            list = sorted;
        }
        string section = null; int n = 0;
        List<GameObject> fold = null;
        foreach (var e in list)
        {
            string sec = e.Section;
            if (sec != section)
            {
                section = sec; fold = null;
                var h = UnityEngine.Object.Instantiate(_headerT, content);
                h.name = "GRV header";
                Shrink(SetText(Child(h.transform, "Action TXT"), Titles.TryGetValue(section, out var title) ? title : section.ToUpperInvariant()));
                n = 0;
            }
            bool adv = k != 2 && IsAdvanced(e);
            if (adv && fold == null) { fold = new List<GameObject>(); AddFold(content, fold, n++); }
            var row = AddRow(content, e, k, n++);
            if (adv) { fold.Add(row); row.SetActive(false); }
        }
    }

    static void AddFold(Transform content, List<GameObject> rows, int index)
    {
        var go = UnityEngine.Object.Instantiate(_rowT, content);
        go.name = "GRV advanced";
        var t = go.transform;
        var dark = Child(t, "Background Dark"); var light = Child(t, "Background Light");
        if (dark != null && light != null) { dark.gameObject.SetActive(index % 2 == 0); light.gameObject.SetActive(index % 2 == 1); }
        SetText(Child(t, "Action TXT"), "Advanced");
        var cell = Child(t, "Keybind Button");
        var text = Child(cell, "Text (TMP)")?.GetComponent<TMP_Text>();
        if (text != null) text.text = "SHOW";
        bool open = false;
        Clickable(cell, () =>
        {
            open = !open;
            foreach (var r in rows) if (r != null) r.SetActive(open);
            if (text != null) text.text = open ? "HIDE" : "SHOW";
        });
        go.SetActive(true);
    }

    static GameObject AddRow(Transform content, ConfigEntryBase e, int k, int index)
    {
        var go = UnityEngine.Object.Instantiate(_rowT, content);
        go.name = "GRV row";
        var t = go.transform;
        var dark = Child(t, "Background Dark"); var light = Child(t, "Background Light");
        if (dark != null && light != null) { dark.gameObject.SetActive(index % 2 == 0); light.gameObject.SetActive(index % 2 == 1); }
        string label = Panel.Pretty(e);
        if (e is PrefKey pk && pk.NeedsRestart) label += " (restart)";
        Shrink(SetText(Child(t, "Action TXT"), label));

        var cell = Child(t, "Keybind Button");
        var row = new Row { Entry = e, Value = Child(cell, "Text (TMP)")?.GetComponent<TMP_Text>() };
        object v = e.BoxedValue;
        bool number = (v is float || v is int) && e.Range != null;
        bool choice = v is int && e.Range != null && Choices.TryGetValue(e.Key, out var names) && AddDropdown(t, row, cell, names);
        if (choice) number = false;

        var resetCell = UnityEngine.Object.Instantiate(cell.gameObject, t).transform;
        resetCell.name = "GRV reset";
        resetCell.gameObject.SetActive(true);
        var rr = resetCell.TryCast<RectTransform>();
        rr.anchoredPosition = new Vector2(number ? 304f : 254f, 0f);
        rr.sizeDelta = new Vector2(number ? 104f : 204f, 49f);
        SetText(Child(resetCell, "Text (TMP)"), "RESET");
        Clickable(resetCell, () => { if (_binding == row) _binding = null; row.Entry.BoxedValue = row.Entry.DefaultValue; Show(row); });

        if (choice) { }
        else if (number && _sliderT != null)
        {
            cell.gameObject.SetActive(false);
            var sgo = UnityEngine.Object.Instantiate(_sliderT, t);
            sgo.name = "GRV value";
            var srt = sgo.transform.TryCast<RectTransform>();
            srt.anchorMin = srt.anchorMax = new Vector2(0.5f, 0.5f);
            srt.pivot = new Vector2(0.5f, 0.5f);
            srt.anchoredPosition = new Vector2(34f, 0f);
            srt.sizeDelta = new Vector2(170f, 88f);
            var s = sgo.GetComponent<Slider>();
            s.onValueChanged = new Slider.SliderEvent();
            s.wholeNumbers = v is int;
            s.minValue = e.Range.Lo; s.maxValue = e.Range.Hi;
            s.interactable = true;
            row.Slider = s; row.Step = v is int ? 1f : Panel.Step(e);
            var num = Child(sgo.transform, "Value");
            if (num != null)
            {
                var nrt = num.TryCast<RectTransform>();
                nrt.anchorMin = nrt.anchorMax = new Vector2(1f, 0.5f);
                nrt.pivot = new Vector2(0f, 0.5f);
                nrt.anchoredPosition = new Vector2(8f, 0f);
                nrt.sizeDelta = new Vector2(70f, 49f);
                row.Value = num.GetComponent<TMP_Text>();
                if (row.Value != null) { try { row.Value.alignment = TextAlignmentOptions.Left; } catch { } Shrink(row.Value); }
            }
            s.onValueChanged.AddListener((UnityAction<float>)(Action<float>)(x => Slide(row, x)));
        }
        else if (v is bool) AddSwitch(t, row, cell);
        else if (v is KeyCode) Clickable(cell, () => { _binding = row; _bindFrame = Time.frameCount; if (row.Value != null) row.Value.text = "PRESS A KEY"; });
        else if (number) Clickable(cell, () => { Bump(row); Show(row); });
        else Clickable(cell, () => { });

        _rows[k].Add(row);
        Show(row);
        go.SetActive(true);
        return go;
    }

    static RectTransform Box(string name, Transform parent, float x, float w, float h, out Image img)
    {
        var go = new GameObject(name);
        go.layer = parent.gameObject.layer;
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(x, 0f);
        rt.sizeDelta = new Vector2(w, h);
        img = go.AddComponent<Image>();
        if (_round != null) { img.sprite = _round; img.type = Image.Type.Sliced; }
        return rt;
    }

    static void AddSwitch(Transform t, Row row, Transform cell)
    {
        cell.gameObject.SetActive(false);
        row.Value = null;
        var pill = Box("GRV switch", t, 44f, 56f, 28f, out var pillImg);
        row.Knob = Box("Knob", pill, 0f, 22f, 22f, out var knobImg);
        knobImg.color = Color.white; knobImg.raycastTarget = false;
        row.Pill = pillImg;
        Clickable(pill, () => { row.Entry.BoxedValue = !(bool)row.Entry.BoxedValue; Show(row); });
    }

    static bool AddDropdown(Transform t, Row row, Transform cell, string[] names)
    {
        try
        {
            var drop = Child(t, "Activation Dropdown");
            var dd = drop != null ? drop.GetComponent<TMP_Dropdown>() : null;
            if (dd == null) return false;
            int lo = Mathf.RoundToInt(row.Entry.Range.Lo);
            var rt = drop.TryCast<RectTransform>();
            rt.anchoredPosition = new Vector2(44f, 0f);
            rt.sizeDelta = new Vector2(204f, 49f);
            dd.onValueChanged = new TMP_Dropdown.DropdownEvent();
            dd.ClearOptions();
            var list = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var n in names) list.Add(n);
            dd.AddOptions(list);
            dd.SetValueWithoutNotify(Mathf.Clamp((int)row.Entry.BoxedValue - lo, 0, names.Length - 1));
            dd.RefreshShownValue();
            dd.interactable = true;
            dd.onValueChanged.AddListener((UnityAction<int>)(Action<int>)(i => { if (!_updating) row.Entry.BoxedValue = i + lo; }));
            row.Drop = dd; row.DropBase = lo; row.Value = null;
            cell.gameObject.SetActive(false);
            drop.gameObject.SetActive(true);
            return true;
        }
        catch (Exception e) { Say("dropdown for " + row.Entry.Key + " failed: " + e.Message); return false; }
    }

    static void Bump(Row row)
    {
        var e = row.Entry; float v = Convert.ToSingle(e.BoxedValue);
        float step = e.BoxedValue is int ? 1f : Panel.Step(e) * 5f;
        v += step; if (v > e.Range.Hi + 0.0001f) v = e.Range.Lo;
        if (e.BoxedValue is int) e.BoxedValue = Mathf.RoundToInt(v); else e.BoxedValue = v;
    }

    static void Slide(Row row, float x)
    {
        if (_updating) return;
        var e = row.Entry;
        if (e.BoxedValue is int) e.BoxedValue = Mathf.RoundToInt(x);
        else e.BoxedValue = Mathf.Clamp(Mathf.Round(x / row.Step) * row.Step, e.Range.Lo, e.Range.Hi);
        if (row.Value != null) row.Value.text = ValueText(e);
    }

    static string ValueText(ConfigEntryBase e)
    {
        var shown = e.Display;
        if (shown != null) return shown.ToUpperInvariant();
        object v = e.BoxedValue;
        if (v is bool b) return b ? "ON" : "OFF";
        if (v is float f) return f.ToString(Mathf.Abs(f) < 10f && f != Mathf.Round(f) ? "0.00" : "0.#");
        return (v?.ToString() ?? "").ToUpperInvariant();
    }

    static void Show(Row row)
    {
        if (row == _binding) return;
        if (row.Value != null) row.Value.text = ValueText(row.Entry);
        if (row.Pill != null)
        {
            bool on = (bool)row.Entry.BoxedValue;
            row.Pill.color = on ? SwitchOn : SwitchOff;
            if (row.Knob != null) row.Knob.anchoredPosition = new Vector2(on ? 14f : -14f, 0f);
        }
        if (row.Drop != null)
        {
            int want = (int)row.Entry.BoxedValue - row.DropBase;
            if (row.Drop.value != want)
            {
                _updating = true;
                try { row.Drop.SetValueWithoutNotify(want); row.Drop.RefreshShownValue(); } finally { _updating = false; }
            }
        }
        if (row.Slider != null)
        {
            _updating = true;
            try { row.Slider.value = Convert.ToSingle(row.Entry.BoxedValue); } finally { _updating = false; }
        }
    }

    static void Refresh()
    {
        var list = _rows[(int)_mode];
        if (list == null) return;
        foreach (var r in list) Show(r);
    }

    static void Capture()
    {
        if (Time.frameCount <= _bindFrame + 1) return;
        var row = _binding;
        if (Input.GetKeyDown(KeyCode.Escape)) { _binding = null; Show(row); return; }
        if (Input.GetKeyDown(KeyCode.Backspace)) { _binding = null; row.Entry.BoxedValue = KeyCode.None; Show(row); return; }
        foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
        {
            if (k == KeyCode.None || k == KeyCode.Mouse0 || !Input.GetKeyDown(k)) continue;
            _binding = null;
            row.Entry.BoxedValue = k;
            Show(row);
            Say("key for " + row.Entry.Key + " set to " + k);
            return;
        }
    }

    static void Undo(int k)
    {
        _binding = null;
        foreach (var r in _rows[k]) if (_snapshot.TryGetValue(r.Entry, out var v)) r.Entry.BoxedValue = v;
        Refresh();
        Say("changes undone");
    }

    static void ResetAll(int k)
    {
        _binding = null;
        foreach (var r in _rows[k]) r.Entry.BoxedValue = r.Entry.DefaultValue;
        Refresh();
        Say("reset to default");
    }

    static void Save(int k)
    {
        _binding = null;
        Plugin.Config.Save();
        try { MelonLoader.MelonPreferences.Save(); } catch { }
        Snapshot(k);
        if (_saveText[k] != null) { _saveText[k].text = "SAVED"; _savedPage = k; _savedUntil = Time.unscaledTime + 1.5f; }
        Say("settings saved");
    }

    static void Snapshot(int k)
    {
        foreach (var r in _rows[k]) _snapshot[r.Entry] = r.Entry.BoxedValue;
    }

    static void ReadStyle(Transform button, Style style)
    {
        try
        {
            style.Parts.Clear();
            var b = button.GetComponent<Button>();
            if (b != null) style.Interactable = b.interactable;
            foreach (var g in button.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null) continue;
                style.Parts.Add(new Part { Name = g.transform == button ? "" : g.name, Color = g.color, Active = g.gameObject.activeSelf, Enabled = g.enabled });
            }
            style.Ok = style.Parts.Count > 0;
        }
        catch { style.Ok = false; }
    }

    static void ApplyStyle(Transform button, Style style, bool lockToo)
    {
        if (button == null || !style.Ok) return;
        try
        {
            if (lockToo) { var b = button.GetComponent<Button>(); if (b != null) b.interactable = style.Interactable; }
            foreach (var g in button.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null) continue;
                string name = g.transform == button ? "" : g.name;
                foreach (var p in style.Parts)
                {
                    if (p.Name != name) continue;
                    g.color = p.Color; g.enabled = p.Enabled;
                    if (name != "") g.gameObject.SetActive(p.Active);
                    break;
                }
            }
        }
        catch { }
    }

    static void AddNav(GameObject page, bool isControls, bool isAudio)
    {
        var root = page.transform;
        var control = Child(root, "Control Button");
        var video = Child(root, "Video Settings Button (1)");
        var audio = Child(root, "Audio Settings Button (2)");
        if (control == null || video == null || audio == null) { Say("side buttons not found on " + page.name); return; }
        var a = audio.TryCast<RectTransform>(); var vd = video.TryCast<RectTransform>();
        float dy = a.anchoredPosition.y - vd.anchoredPosition.y;
        var source = isAudio ? control : audio;
        Texture icon = null;
        try { icon = Child(control, "RawImage (1)")?.GetComponent<RawImage>()?.texture; } catch { }
        if (isControls)
        {
            ReadStyle(control, _selected); ReadStyle(audio, _plain); _navControl = control;
            Say("side button parts: " + Parts(control.gameObject) + " | selected locked=" + !_selected.Interactable);
        }

        for (int k = 1; k <= 2; k++)
        {
            var go = UnityEngine.Object.Instantiate(source.gameObject, root);
            go.name = "GRV " + PageName[k] + " Button";
            var rt = go.transform.TryCast<RectTransform>();
            rt.anchoredPosition = a.anchoredPosition + new Vector2(0f, dy * k);
            go.transform.SetSiblingIndex(audio.GetSiblingIndex() + k);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null || Array.IndexOf(Allowed, TypeName(mb)) >= 0) continue;
                try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
            }
            SetText(Child(go.transform, "Text (TMP)"), PageName[k]);
            try { var ri = Child(go.transform, "RawImage (1)")?.GetComponent<RawImage>(); if (ri != null && icon != null) ri.texture = icon; } catch { }
            int which = k; var from = root; bool here = isControls;
            Clickable(go.transform, () => Open(from, here, which));
            go.SetActive(true);
            if (isControls) _navOurs[k] = go.transform;
        }

        if (!isControls) return;
        foreach (var name in new[] { "Control Button", "Player Settings Button", "Video Settings Button (1)", "Audio Settings Button (2)", "Exit (2)", "Lobby Button", "ExitButton" })
        {
            var b = Child(root, name)?.GetComponent<Button>();
            if (b != null) b.onClick.AddListener((UnityAction)(Action)(() => SetMode(Mode.Native)));
        }
    }

    static void Open(Transform from, bool onControls, int which)
    {
        try
        {
            _stage = "open page";
            if (!onControls)
            {
                var b = Child(from, "Control Button")?.GetComponent<Button>();
                if (b != null) b.onClick.Invoke();
            }
            SetMode((Mode)which);
        }
        catch (Exception e) { Plugin.Out.LogWarning("[Menu] open: " + e.Message); }
    }

    static void SetMode(Mode m)
    {
        if (_page == null) return;
        _binding = null;
        if (_native != null) _native.SetActive(m == Mode.Native);
        for (int k = 1; k <= 2; k++) if (_ours[k] != null) _ours[k].SetActive((int)m == k);
        ApplyStyle(_navControl, m == Mode.Native ? _selected : _plain, true);
        for (int k = 1; k <= 2; k++) ApplyStyle(_navOurs[k], (int)m == k ? _selected : _plain, false);
        if (_title != null) _title.text = m == Mode.Native ? _titleNative : PageName[(int)m];
        if (m != Mode.Native && m != _mode) Snapshot((int)m);
        _mode = m;
        if (m != Mode.Native) Refresh();
    }
}
