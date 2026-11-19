using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace GhostReconView;

internal class PrefKey : ConfigEntryBase
{
    readonly Func<string> _get;
    readonly Action<string> _set;
    readonly string _default;
    internal bool NeedsRestart;
    internal PrefKey(Func<string> get, Action<string> set, string def) { _get = get; _set = set; _default = def; }
    static KeyCode Parse(string s) { try { return (KeyCode)Enum.Parse(typeof(KeyCode), s, true); } catch { return KeyCode.None; } }
    public override Type SettingType => typeof(KeyCode);
    public override string Display => _get();
    public override object DefaultValue => Parse(_default);
    public override object BoxedValue
    {
        get => Parse(_get());
        set { try { _set(((KeyCode)value).ToString()); } catch { } }
    }
}

internal class PrefBool : ConfigEntryBase
{
    readonly MelonPreferences_Entry<bool> _e;
    readonly Action _changed;
    internal PrefBool(MelonPreferences_Entry<bool> e, Action changed) { _e = e; _changed = changed; }
    public override Type SettingType => typeof(bool);
    public override object DefaultValue => _e.DefaultValue;
    public override object BoxedValue
    {
        get => _e.Value;
        set { _e.Value = (bool)value; try { _e.Category.SaveToFile(false); } catch { } try { _changed?.Invoke(); } catch { } }
    }
}

internal static class OdgBridge
{
    static bool _loaded;
    const string Own = "GhostReconView";
    const string Restart = "  Takes effect after you restart the game.";

    static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>
    {
        { "ThirdPerson.ToggleKey", "V" }, { "ThirdPerson.SwapShoulderKey", "Mouse2" }, { "OperatorCompass.ToggleKey", "F4" },
        { "FireteamRoles.TreatKey", "Alpha9" }, { "FireteamRoles.GrabKey", "Alpha0" }, { "FireteamRoles.SniperMarkKey", "Minus" },
        { "ODGWatch.LookKey", "J" }
    };

    static readonly Dictionary<string, string> Sections = new Dictionary<string, string>
    {
        { "ThirdPerson", "THIRD PERSON" }, { "FireteamRoles", "FIRETEAM ROLES" }, { "ODGChat", "CHAT" }, { "ODGWatch", "WATCH" },
        { "OperatorCompass", "COMPASS" }, { "Vehicles", "VEHICLES" }, { "ODGDiagnostics", "MAP MARK" }
    };

    static string Words(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]) && s[i - 1] != '_') sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    static string KeyLabel(string id)
    {
        int cut = id.LastIndexOf("Key", StringComparison.Ordinal);
        string s = cut > 0 ? id.Substring(0, cut) : id;
        return Words(s) + " key";
    }

    static void RefreshThirdPerson()
    {
        var t = AccessTools.TypeByName("ThirdPerson.ThirdPersonMod");
        if (t == null) return;
        var inst = AccessTools.Field(t, "_instance")?.GetValue(null);
        if (inst != null) AccessTools.Method(t, "ApplyRigSettings")?.Invoke(inst, null);
    }

    static string Text(string category, string id)
    {
        try { return MelonPreferences.GetEntry<string>(category, id)?.Value; } catch { return null; }
    }

    internal static event Action Loaded;

    internal static void Load(ConfigFile cfg)
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            try { Loaded?.Invoke(); } catch (Exception e) { Plugin.Out.LogDebug("[Menu] " + e.Message); }
            void Toggle(string id, string label)
            {
                MelonPreferences_Entry<bool> e = null;
                try { e = MelonPreferences.GetEntry<bool>("ThirdPerson", id); } catch { }
                if (e == null) return;
                cfg.Entries.Add(new PrefBool(e, RefreshThirdPerson) { Section = "THIRD PERSON", Key = id, Label = label, Help = e.Description ?? "" });
            }
            Toggle("SqueezeToEye", "First person when squeezed (doors)");
            Toggle("WallCollision", "Camera stays out of walls");
            Toggle("AdsToFirstPerson", "Aiming drops to first person");

            try
            {
                var looks = MelonPreferences.GetEntry<bool>("ODG_Opfor", "Looks");
                if (looks != null) cfg.Entries.Add(new PrefBool(looks, null) { Section = "ENEMIES", Key = "Looks", Label = "Enemies dressed by difficulty (restart)", Help = looks.Description ?? "" });
            }
            catch { }

            var cats = new List<MelonPreferences_Category>();
            foreach (var cat in MelonPreferences.Categories) if (cat != null && cat.Identifier != Own) cats.Add(cat);
            cats.Sort((a, b) => (a.Identifier == "ThirdPerson" ? 0 : 1).CompareTo(b.Identifier == "ThirdPerson" ? 0 : 1));
            foreach (var cat in cats)
            {
                string section = Sections.TryGetValue(cat.Identifier, out var title) ? title : Words(cat.Identifier.Replace("ODG_", "").Replace("ODG", "")).ToUpperInvariant();
                foreach (var entry in cat.Entries)
                {
                    string id = entry.Identifier ?? "";
                    if (id.IndexOf("Key", StringComparison.Ordinal) < 0 || id.Contains("Dump") || id.Contains("Diag")) continue;
                    string full = cat.Identifier + "." + id;
                    var owner = cat;
                    PrefKey key = null;
                    if (entry is MelonPreferences_Entry<string> se)
                    {
                        if (!id.EndsWith("Key", StringComparison.Ordinal)) continue;
                        key = new PrefKey(() => se.Value, v => { se.Value = v; owner.SaveToFile(false); }, Defaults.TryGetValue(full, out var d) ? d : se.DefaultValue) { NeedsRestart = true };
                    }
                    else if (entry is MelonPreferences_Entry<KeyCode> ke)
                    {
                        key = new PrefKey(() => ke.Value.ToString(), v => { ke.Value = (KeyCode)Enum.Parse(typeof(KeyCode), v, true); owner.SaveToFile(false); }, Defaults.TryGetValue(full, out var d) ? d : ke.DefaultValue.ToString());
                    }
                    if (key == null) continue;
                    key.Section = section; key.Key = full; key.Label = KeyLabel(id);
                    key.Help = (entry.Description ?? "") + (key.NeedsRestart ? Restart : "");
                    cfg.Entries.Add(key);
                }
            }
        }
        catch (Exception e) { Plugin.Out.LogWarning("[Panel] ODG settings: " + e.Message); }
    }
}
