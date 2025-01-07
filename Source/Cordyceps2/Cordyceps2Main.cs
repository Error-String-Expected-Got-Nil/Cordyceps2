using System;
using BepInEx;
using UnityEngine;

namespace Cordyceps2;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Cordyceps2Main : BaseUnityPlugin
{
    public const string PluginGuid = "esegn.cordyceps2";
    public const string PluginName = "Cordyceps2 TAS Tool";
    public const string PluginVersion = "1.0.0";

    // Only valid after initialization, when all mods are loaded.
    public static string ModPath => ModManager.ActiveMods.Find(mod => mod.id == PluginGuid)?.path;
    
    public static Cordyceps2Main Instance;

    private static bool _initialized;
    private static bool _postInitialized;
    
    private void OnEnable()
    {
        Instance = this;
        
        On.RainWorld.OnModsInit += RainWorld_OnModsInit_Hook;
        On.RainWorld.PostModsInit += RainWorld_PostModsInit_Hook;
    }

    private static void RainWorld_OnModsInit_Hook(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        if (_initialized) return;

        try
        {
            Log("Registering hooks.");
            On.RoomCamera.ctor += InfoPanel.RoomCamera_ctor_Hook;
            On.RoomCamera.ClearAllSprites += InfoPanel.RoomCamera_ClearAllSprites_Hook;
            On.RainWorldGame.GrafUpdate += InfoPanel.RainWorldGame_GrafUpdate_Hook;
            
            On.RainWorldGame.Update += TimeControl.RainWorldGame_Update_Hook;
            On.MoreSlugcats.SpeedRunTimer.GetTimerTickIncrement +=
                TimeControl.MoreSlugcats_SpeedRunTimer_GetTimerTickIncrement_Hook;
            IL.RainWorldGame.RawUpdate += TimeControl.RainWorldGame_RawUpdate_ILHook;
            
            MachineConnector.SetRegisteredOI("esegn.cordyceps2", Cordyceps2Settings.Instance);

            _initialized = true;
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception during initialization: {e}");
        }
    }

    private static void RainWorld_PostModsInit_Hook(On.RainWorld.orig_PostModsInit orig, RainWorld self)
    {
        orig(self);
        if (_postInitialized) return;

        try
        {
            Log("Registering libav binaries.");
            Recording.Initialize();

            _postInitialized = true;
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception during post-initialization: {e}");
        }
    }
        
    private static void Log(string str) => Debug.Log($"[Cordyceps2] {str}");
}