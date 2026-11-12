using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using OperatorModControls;

namespace GhostReconView;

internal static class MenuBridge
{
    const string Bass = "community.operator.thirdperson";
    const string Group = "Ghost Recon (3rd Person View)";
    internal static bool Dirty;
    static bool _hooked;

    internal static void Register()
    {
        string id = Plugin.Guid;
        var st = AccessTools.TypeByName("OperatorTPS.Settings");
        T F<T>(string n) where T : class => st == null ? null : AccessTools.Field(st, n)?.GetValue(null) as T;

        SettingsRegistry.Unregister(id);
        BindingRegistry.Unregister(id);
        if (st != null) { SettingsRegistry.Unregister(Bass); BindingRegistry.Unregister(Bass); }

        int o = 0;
        void Key(string label, ConfigEntry<KeyCode> e, string help) { if (e != null) BindingRegistry.Register(id, Group, label, e, help, o++); }
        void Tog(string label, ConfigEntry<bool> e, string help, bool adv)
        {
            if (e == null) return;
            SettingsRegistry.RegisterToggle(id, Group, label, e, help, o++);
            if (adv) SettingsRegistry.SetAdvanced(id, e, false);
        }
        void Sld(string label, ConfigEntry<float> e, float step, string help, string fmt, string suf, bool adv)
        {
            if (e == null) return;
            SettingsRegistry.RegisterSlider(id, Group, label, e, step, help, o++, fmt, suf);
            if (adv) SettingsRegistry.SetAdvanced(id, e, false);
        }

        Tog("Crosshair", F<ConfigEntry<bool>>("CrosshairOn"), "Crosshair on/off. No key needed.", false);
        Key("Toggle third person", F<ConfigEntry<KeyCode>>("ToggleKey"), "Switch between first and third person.");
        Key("3rd person aim switch" + (Plugin.RequireShift.Value ? "  (Shift +)" : ""), Plugin.SwitchKey, "Hold Shift while you press the new key to make it Shift + key.");
        Key("Swap shoulder", F<ConfigEntry<KeyCode>>("ShoulderKey"), "Move the camera to your other shoulder.");
        Key("Camera closer (hold)", F<ConfigEntry<KeyCode>>("CloserKey"), "Hold to move the camera closer.");
        Key("Camera farther (hold)", F<ConfigEntry<KeyCode>>("FartherKey"), "Hold to move the camera farther away.");

        Tog("Laser beam", LaserBeams.On, "Show the laser beam.", false);
        Sld("Laser thickness", LaserBeams.Thickness, 5f, "How thick the laser line is.", "0", "%", false);
        Sld("Laser strength", LaserBeams.Strength, 5f, "Up to 100 = more solid. Above 100 = a little self-brightness.", "0", "%", false);
        Sld("Laser shimmer", LaserBeams.Shimmer, 5f, "How much the laser shimmers when you move.", "0", "%", false);
        Sld("Laser grain", LaserBeams.Grain, 5f, "Lower = smoother line, higher = more dust and cuts.", "0", "%", false);
        Sld("Laser paleness", LaserBeams.Paleness, 5f, "Lower = deeper color, higher = paler.", "0", "%", false);
        Sld("Laser dot size", LaserBeams.DotSize, 10f, "Size of the laser dot.", "0", "%", false);
        if (LaserBeams.ColorChoice != null) SettingsRegistry.RegisterInteger(id, Group, "Laser color  (0 game, 1 green, 2 red, 3 yellow, 4 blue)", LaserBeams.ColorChoice, "0 uses the game's laser color.", o++);
        Tog("Night vision in 3rd person", F<ConfigEntry<bool>>("NvgThirdPerson"), "NVG effect on the whole screen in third person.", false);
        Sld("Camera distance", F<ConfigEntry<float>>("Distance"), 0.1f, "How far behind you.", "0.0", " m", false);
        Sld("Shoulder offset", F<ConfigEntry<float>>("Side"), 0.05f, "How far to the side.", "0.00", " m", false);
        Sld("Camera height", F<ConfigEntry<float>>("Height"), 0.05f, "Above your eyes.", "0.00", " m", false);
        Sld("Field of view", F<ConfigEntry<float>>("Fov"), 1f, "Third-person field of view.", "0", "", false);

        Tog("Aim zoom", F<ConfigEntry<bool>>("AdsZoom"), "Aiming moves the camera in over your shoulder.", false);
        Tog("Tighten up close", F<ConfigEntry<bool>>("CloseQuarters"), "Camera moves in when what you aim at is close.", false);
        Tog("Wall collision", F<ConfigEntry<bool>>("WallCollision"), "Keep the camera out of walls.", false);
        Tog("Hide body when too close", F<ConfigEntry<bool>>("FadeBody"), "Hides your character if the camera is squeezed against it.", false);
        Sld("Aim distance", F<ConfigEntry<float>>("AdsDistance"), 0.1f, "Camera distance while aiming.", "0.0", " m", false);
        Sld("Aim zoom amount", F<ConfigEntry<float>>("AdsZoomAmount"), 0.05f, "1 = no zoom.", "0.00", "x", false);
        Sld("Follow smoothness", F<ConfigEntry<float>>("FollowSmoothness"), 0.5f, "0 = rigid, 10 = floaty.", "0.#", "", false);
        Sld("Turn smoothness", F<ConfigEntry<float>>("TurnSmoothness"), 0.5f, "0 = instant, 10 = floaty.", "0.#", "", false);
        Sld("Remove head bob", F<ConfigEntry<float>>("BobReduction"), 5f, "Keeps bob/recoil kick out of the camera.", "0", "%", false);
        if (Plugin.CrosshairColor != null) SettingsRegistry.RegisterInteger(id, Group, "Crosshair color  (0 white, 1 green, 2 red, 3 yellow, 4 blue)", Plugin.CrosshairColor, "Crosshair color.", o++);
        Tog("Crosshair outline", F<ConfigEntry<bool>>("Outline"), "Black edge for bright backgrounds.", false);
        Sld("Crosshair size", F<ConfigEntry<float>>("Size"), 1f, "Line length.", "0", "", false);
        Sld("Crosshair thickness", F<ConfigEntry<float>>("Thickness"), 1f, "Line thickness.", "0", "", false);
        Sld("Crosshair gap", F<ConfigEntry<float>>("Gap"), 1f, "Space in the middle.", "0", "", false);
        Sld("Crosshair opacity", F<ConfigEntry<float>>("Opacity"), 1f, "0 = invisible, 100 = solid.", "0", "%", false);

        const string Handling = "Smart Reload (COD)";
        if (SmartReload.Speed != null)
            SettingsRegistry.RegisterSlider(id, Handling, "Reload speed", SmartReload.Speed, 10f, "C = one reload key: racks an empty chamber, clears jams, reloads, chambers an empty gun. 100 = normal speed.", o++, "0", "%");

        const string Move = "Movement";
        if (AutoLean.On != null)
        {
            SettingsRegistry.RegisterToggle(id, Move, "Auto lean", AutoLean.On, "Your operator leans by himself when you aim past the edge of cover: your eyes see the room, one shoulder is still behind the wall. Lets go when you step clear. Your own lean buttons always win.", o++);
            SettingsRegistry.RegisterSlider(id, Move, "Auto lean reach", AutoLean.Reach, 0.1f, "Max distance between your shoulder and the wall edge for the lean to kick in. Eyes clear + one shoulder blocked within this distance = lean to that side. Bigger = leans from farther away.", o++, "0.0", " m");
        }

        if (_hooked) return;
        _hooked = true;
        // one color pick drives the third-person mod's red/green/blue entries
        var cr = F<ConfigEntry<float>>("Red"); var cg = F<ConfigEntry<float>>("Green"); var cb = F<ConfigEntry<float>>("Blue");
        void ApplyColor()
        {
            if (cr == null || cg == null || cb == null || Plugin.CrosshairColor == null) return;
            var c = Plugin.CrosshairColor.Value switch { 1 => (60f, 255f, 60f), 2 => (255f, 60f, 60f), 3 => (255f, 230f, 40f), 4 => (60f, 160f, 255f), _ => (255f, 255f, 255f) };
            cr.Value = c.Item1; cg.Value = c.Item2; cb.Value = c.Item3;
        }
        if (Plugin.CrosshairColor != null) { Plugin.CrosshairColor.SettingChanged += (object s, EventArgs a) => ApplyColor(); ApplyColor(); }
        Plugin.SwitchKey.SettingChanged += (object sender, EventArgs args) =>
        {
            bool shift = Plugin.ShiftHeld();
            Plugin.RequireShift.Value = shift || Plugin.SwitchKey.Value == Plugin.BassToggleKey;
            Dirty = true;
        };
    }
}
