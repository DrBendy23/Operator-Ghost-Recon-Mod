using MelonLoader;

namespace GhostReconView;

internal static class MenuScreen
{
    static int _tries;
    static bool _done;

    internal static void Tick()
    {
        if (_done || _tries > 600) return;
        _tries++;
        try
        {
            var bg = MelonPreferences.GetEntry<bool>("HomeScreen", "Background");
            if (bg == null) return;
            _done = true;
            string mark = System.IO.Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "GhostReconView_menu_set.txt");
            if (System.IO.File.Exists(mark)) return;
            System.IO.File.WriteAllText(mark, System.DateTime.Now.ToString("s"));
            if (!bg.Value) return;
            bg.Value = false;
            var music = MelonPreferences.GetEntry<bool>("HomeScreen", "Music");
            if (music != null && !music.Value) music.Value = true;
            MelonPreferences.Save();
            Plugin.Out.LogInfo("[Menu] ODG home screen picture switched off: the original OPERATOR menu shows from the next start, ODG music stays on");
        }
        catch { }
    }
}
