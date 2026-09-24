using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace SpireReplay.SpireReplayCode;

//You're recommended but not required to keep all your code in this package and all your assets in the SpireReplay folder.
[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "SpireReplay"; //At the moment, this is used only for the Logger and harmony names.

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        var assembly = Assembly.GetExecutingAssembly();

        //If you want to use scripts defined in your mod for Godot scenes, uncomment the following line.
        //Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(assembly);
     
        Harmony harmony = new(ModId);

        try
        {
            harmony.PatchAll(assembly);
            BattleRecorder.Initialize();
            try { RunLibrary.Trim(ProjectSettings.GlobalizePath("user://SpireReplay/recordings")); }
            catch (Exception e) { Logger.Warn("History cleanup failed: " + e.Message); }
            Logger.Info("SpireReplay 0.10.2: battle, choice, potion, reward, shop and rest-site recording enabled.");
        }
        catch (Exception error)
        {
            // A game update must not prevent the game from loading if a hook changes.
            harmony.UnpatchAll(ModId);
            Logger.Error($"SpireReplay could not initialize: {error}");
        }
    }
}





