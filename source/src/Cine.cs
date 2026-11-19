using BepInEx.Configuration;
using BepInEx.Logging;

namespace CinematicLook;

internal static class Plugin
{
    internal static ManualLogSource Out;
    internal static ConfigEntry<bool> On;
    internal static ConfigEntry<int> Preset;
    internal static ConfigEntry<float> Strength, Contrast, Saturation, Warmth, Vignette, Grain, Bloom, Exposure;

    internal static void Init(ConfigFile cfg, ManualLogSource log)
    {
        Out = log;
        On = cfg.Bind("Cinematic", "Enabled", true, "Cinematic look on or off.");
        Preset = cfg.Bind("Cinematic", "Preset", 1, new ConfigDescription("0 custom, 1 realism, 2 film, 3 warm, 4 cold, 5 noir. Picking a preset sets the sliders below.", new AcceptableValueRange<int>(0, 5)));
        Strength = cfg.Bind("Cinematic", "Strength", 100f, new ConfigDescription("How much of the look is applied, percent.", new AcceptableValueRange<float>(0f, 100f)));
        Exposure = cfg.Bind("Cinematic", "Exposure", 0f, new ConfigDescription("Brighter or darker overall, in stops.", new AcceptableValueRange<float>(-2f, 2f)));
        Contrast = cfg.Bind("Cinematic", "Contrast", 8f, new ConfigDescription("Percent.", new AcceptableValueRange<float>(-50f, 80f)));
        Saturation = cfg.Bind("Cinematic", "Saturation", -6f, new ConfigDescription("Percent.", new AcceptableValueRange<float>(-100f, 60f)));
        Warmth = cfg.Bind("Cinematic", "Warmth", 0f, new ConfigDescription("Colour temperature, minus is cooler.", new AcceptableValueRange<float>(-40f, 40f)));
        Vignette = cfg.Bind("Cinematic", "Vignette", 8f, new ConfigDescription("Dark edges, percent.", new AcceptableValueRange<float>(0f, 70f)));
        Grain = cfg.Bind("Cinematic", "Grain", 6f, new ConfigDescription("Film grain, percent.", new AcceptableValueRange<float>(0f, 100f)));
        Bloom = cfg.Bind("Cinematic", "Bloom", 6f, new ConfigDescription("Glow on bright light, percent.", new AcceptableValueRange<float>(0f, 60f)));
    }
}
