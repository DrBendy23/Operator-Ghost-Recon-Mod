global using Il2Cpp;
global using CMF = Il2CppCMF;
global using Mirror = Il2CppMirror;
global using GK = Il2CppGK;

using System;
using System.Collections.Generic;
using MelonLoader;

namespace BepInEx.Logging
{
    public class ManualLogSource
    {
        readonly MelonLogger.Instance _log;
        readonly System.Collections.Generic.HashSet<string> _once = new System.Collections.Generic.HashSet<string>();
        public ManualLogSource(MelonLogger.Instance log) { _log = log; }

        public void LogInfo(object o) { var s = o?.ToString() ?? ""; _log.Msg(s); }
        public void LogWarning(object o) { var s = o?.ToString() ?? ""; _log.Warning(s); }
        public void LogDebug(object o)
        {
            var s = o?.ToString() ?? "";
            if (_once.Count < 300 && _once.Add(s)) { _log.Msg("[debug] " + s); }
        }
    }
}

namespace BepInEx.Configuration
{
    public abstract class AcceptableValueBase
    {
        public abstract float Lo { get; }
        public abstract float Hi { get; }
    }

    public class AcceptableValueRange<T> : AcceptableValueBase where T : IComparable
    {
        public T MinValue { get; }
        public T MaxValue { get; }
        public AcceptableValueRange(T min, T max) { MinValue = min; MaxValue = max; }
        public override float Lo => Convert.ToSingle(MinValue);
        public override float Hi => Convert.ToSingle(MaxValue);
    }

    public class ConfigDescription
    {
        public string Description { get; }
        public AcceptableValueBase AcceptableValues { get; }
        public ConfigDescription(string description, AcceptableValueBase values = null) { Description = description; AcceptableValues = values; }
    }

    public abstract class ConfigEntryBase
    {
        public string Section, Key, Help, Label;
        public AcceptableValueBase Range;
        public ConfigFile ConfigFile = ConfigFile.None;
        public ConfigDescription Description => new ConfigDescription(Help ?? "", Range);
        public virtual string Display => null;
        public abstract Type SettingType { get; }
        public abstract object BoxedValue { get; set; }
        public virtual object DefaultValue => BoxedValue;
    }

    public class ConfigEntry<T> : ConfigEntryBase
    {
        readonly MelonPreferences_Entry<string> _text;
        readonly MelonPreferences_Entry<T> _plain;
        T _value;
        readonly T _default;
        public override object DefaultValue => _default;
        public event EventHandler SettingChanged;

        internal ConfigEntry(MelonPreferences_Category cat, string id, T def, string help)
        {
            _default = def;
            if (typeof(T).IsEnum)
            {
                _text = cat.CreateEntry(id, def.ToString(), description: help);
                try { _value = (T)Enum.Parse(typeof(T), _text.Value, true); } catch { _value = def; }
            }
            else
            {
                _plain = cat.CreateEntry(id, def, description: help);
                _value = _plain.Value;
            }
        }

        public T Value
        {
            get => _value;
            set
            {
                if (EqualityComparer<T>.Default.Equals(_value, value)) return;
                _value = value;
                if (_text != null) _text.Value = value.ToString(); else _plain.Value = value;
                ConfigFile.Dirty = true;
                SettingChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public override Type SettingType => typeof(T);
        public override object BoxedValue { get => Value; set => Value = (T)value; }
    }

    public class ConfigFile
    {
        readonly MelonPreferences_Category _cat;
        public readonly List<ConfigEntryBase> Entries = new List<ConfigEntryBase>();
        internal static bool Dirty;

        internal static readonly ConfigFile None = new ConfigFile();
        ConfigFile() { }
        public static readonly List<ConfigFile> All = new List<ConfigFile>();
        public ConfigFile(string category) { _cat = MelonPreferences.CreateCategory(category); All.Add(this); }
        public void Save() { if (_cat != null) { Dirty = true; SaveIfDirty(); } }

        public ConfigEntry<T> Bind<T>(string section, string key, T def, string description)
            => Bind(section, key, def, new ConfigDescription(description));

        public ConfigEntry<T> Bind<T>(string section, string key, T def, ConfigDescription description = null)
        {
            var e = new ConfigEntry<T>(_cat, section + "_" + key, def, description?.Description ?? "")
            {
                Section = section, Key = key, Help = description?.Description ?? "", Range = description?.AcceptableValues, ConfigFile = this
            };
            Entries.Add(e);
            return e;
        }

        public void SaveIfDirty()
        {
            if (!Dirty || _cat == null) return;
            Dirty = false;
            try { _cat.SaveToFile(false); } catch { }
        }
    }
}
