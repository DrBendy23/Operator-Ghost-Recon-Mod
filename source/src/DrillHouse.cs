using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GhostReconView;

internal static class DrillHouse
{
    const string Tag = "# Ghost Recon View drill house";

    static readonly string[] Layout =
    {
        "# Ghost Recon View drill house v6: in through the main door into a U corridor (180 check), out at the far corner, lateral, diagonal and L corridors, T intersection, box, L, linear and irregular rooms, doors opening into rooms, steel poppers",
        "# kind x z yaw [len] [enemy skin]",
        "door -41.125 7.000 0.0",
        "leaf -40.635 6.938 180.0",
        "wall -49.062 7.000 0.0 13.88",
        "wall -39.812 7.000 0.0 0.62",
        "wall -39.500 15.250 270.0 16.50",
        "door -40.500 23.500 0.0",
        "wall -48.750 23.500 0.0 14.50",
        "wall -56.000 15.250 270.0 16.50",
        "door -48.000 8.000 270.0",
        "leaf -47.938 8.490 90.0",
        "door -48.000 13.000 270.0",
        "leaf -47.938 13.490 90.0",
        "wall -48.000 10.500 270.0 3.00",
        "wall -48.000 14.500 270.0 1.00",
        "door -46.000 14.000 270.0",
        "wall -46.000 11.000 270.0 4.00",
        "door -44.375 9.000 0.0",
        "wall -45.688 9.000 0.0 0.62",
        "wall -43.062 9.000 0.0 0.62",
        "wall -42.750 10.100 270.0 6.20",
        "wall -50.000 11.000 0.0 4.00",
        "door -52.000 13.000 270.0",
        "leaf -51.938 13.490 90.0",
        "wall -52.000 11.500 270.0 1.00",
        "wall -52.000 14.500 270.0 1.00",
        "door -54.000 15.000 0.0",
        "leaf -54.490 15.062 0.0",
        "wall -55.500 15.000 0.0 1.00",
        "wall -50.500 15.000 0.0 5.00",
        "wall -42.750 15.000 0.0 6.50",
        "wall -50.500 17.500 225.0 7.07",
        "wall -53.000 20.750 270.0 1.50",
        "door -48.240 17.500 225.0",
        "leaf -47.937 17.110 225.0",
        "wall -46.636 15.896 225.0 2.54",
        "wall -49.844 19.104 225.0 2.54",
        "wall -50.740 20.750 270.0 1.50",
        "door -41.500 18.500 270.0",
        "wall -41.500 16.250 270.0 2.50",
        "wall -41.500 20.500 270.0 2.00",
        "door -54.500 21.500 0.0",
        "wall -55.750 21.500 0.0 0.50",
        "wall -53.250 21.500 0.0 0.50",
        "wall -46.120 21.500 0.0 9.24",
        "enemy -40.300 14.300 200 0 spetsnaz",
        "popper -45.300 9.600 20",
        "enemy -55.300 11.600 90 0 azov",
        "popper -55.300 7.600 45",
        "enemy -51.400 14.400 120 0 spetsnaz",
        "enemy -42.200 20.900 225 0 azov",
        "popper -49.300 20.900 150",
        "enemy -55.300 20.800 135 0 spetsnaz",
        "popper -50.500 15.600 300",
        "enemy -40.500 22.800 200 0 azov",
    };

    static string PathOf() => Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "Mods", "Lounge_Data", "house_default.txt");

    internal static void Install()
    {
        try
        {
            var path = PathOf();
            if (!File.Exists(path)) return;
            var now = File.ReadAllText(path);
            var saved = SavedFirst();
            var want = saved != null ? Tag + " (your saved layout 1)\n" + saved : string.Join("\n", Layout) + "\n";
            bool shipped = now.StartsWith("# the shoot house as the game ships it");
            if (!now.StartsWith(Tag) && !shipped || now == want) return;
            if (shipped) { var keep = Path.Combine(Path.GetDirectoryName(path), "house_default.shipped.txt"); if (!File.Exists(keep)) File.Copy(path, keep); }
            File.WriteAllText(path, want);
            Plugin.Out.LogInfo("[Drill] drill house updated" + (saved != null ? " from your saved layout 1" : ""));
        }
        catch (Exception e) { Plugin.Out.LogInfo("[Drill] " + e.Message); }
    }

    static string SavedFirst()
    {
        var path = Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "UserData", "ODG_Lounge_house_presets.txt");
        if (!File.Exists(path)) return null;
        var sb = new System.Text.StringBuilder();
        bool inOne = false;
        foreach (var raw in File.ReadAllLines(path))
        {
            var t = raw.Trim();
            if (t.StartsWith("[") && t.EndsWith("]")) { if (inOne) break; inOne = t == "[1]"; continue; }
            if (inOne && t.Length > 0 && !t.StartsWith("#")) sb.Append(t).Append('\n');
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    internal static void Doors(string scene, List<Map.Door> into)
    {
        try
        {
            var path = PathOf();
            if (!scene.Contains("Operation") || !File.Exists(path)) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var raw in File.ReadAllLines(path))
            {
                var a = raw.Trim().Split(' ');
                if (a.Length < 4 || a[0] != "door") continue;
                float x = float.Parse(a[1], inv), z = float.Parse(a[2], inv), yaw = float.Parse(a[3], inv) * Mathf.Deg2Rad;
                into.Add(new Map.Door { C = new Vector3(x, 0f, z), H = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw)) });
            }
        }
        catch (Exception e) { Plugin.Out.LogInfo("[Drill] doors: " + e.Message); }
    }
}
