using System;
using BepInEx;
using UnityEngine;

namespace Cordyceps2;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Cordyceps2Init : BaseUnityPlugin
{
    public const string PluginGuid = "esegn.cordyceps2";
    public const string PluginName = "Cordyceps2 TAS Tool";
    public const string PluginVersion = "0.0.0";

    private static bool _initialized;
    
    private void OnEnable() => On.RainWorld.OnModsInit += RainWorld_OnModsInit_Hook;

    private static void RainWorld_OnModsInit_Hook(On.RainWorld.orig_OnModsInit orig, RainWorld self)
    {
        orig(self);
        if (_initialized) return;

        try
        {
            Log("Initializing.");
            
            Log("Registering InfoPanel hooks.");
            On.RoomCamera.ctor += InfoPanel.RoomCamera_ctor_Hook;
            On.RoomCamera.ClearAllSprites += InfoPanel.RoomCamera_ClearAllSprites_Hook;
            On.RainWorldGame.GrafUpdate += InfoPanel.RainWorldGame_GrafUpdate_Hook;
            
            Log("Registering settings.");
            MachineConnector.SetRegisteredOI("esegn.cordyceps2", Cordyceps2Settings.Instance);

            _initialized = true;
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception during initialization: {e}");
        }
    }

    private static void Log(string str) => Debug.Log($"[Cordyceps2] {str}");
}