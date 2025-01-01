using System;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using UnityEngine;

namespace Cordyceps2;

// Mostly handles time control functions, as the name implies, but also has general input handling for lack of a better
// place to put it.
public static class TimeControl
{
    public static int UnmodifiedTickrate = 40;
    public static int DesiredTickrate = 40;
    public static bool TickrateCapOn;
    public static bool TickPauseOn;
    public static bool WaitingForTick;
    public static bool ShowInfoPanel = true;
    public static uint TickCount;
    public static bool TickCounterPaused;
    
    private const float TickrateChangeInitialTime = 0.25f;
    private const float TickrateChangeHoldTickTime = 0.05f;

    private static float _keyHoldStopwatch;

    private static readonly bool[] HeldKeys = new bool[8];
    
    // IL Hook: Handles modifying the tickrate and calling input check function.
    public static void RainWorldGame_RawUpdate_ILHook(ILContext il)
    {
        var cursor = new ILCursor(il);
            
        // Finds `this.oDown = Input.GetKey("o");` in RainWorldGame.RawUpdate
        cursor.GotoNext(MoveType.After,
            x => x.MatchLdarg(0),
            x => x.MatchLdstr("o"),
            x => x.MatchCall<Input>("GetKey"),
            x => x.MatchStfld<RainWorldGame>("oDown")
        );
            
        // Put the current RainWorldGame object onto the stack so we can use it to get the tickrate.
        cursor.Emit(OpCodes.Ldarg, 0);
        // Put the dt argument from the RawUpdate function onto the stack so we can use it for input checks.
        cursor.Emit(OpCodes.Ldarg, 1);
            
        // This code will sit after all vanilla tickrate-modifying code and before any vanilla code which uses
        // the tickrate.
        cursor.EmitDelegate((RainWorldGame game, float dt) =>
        {
            try
            {
                UnmodifiedTickrate = game.framesPerSecond;

                CheckInputs(dt);

                if (CanAffectTickrate()) 
                    game.framesPerSecond = TickPauseOn ? 0 : Math.Min(DesiredTickrate, game.framesPerSecond);
            }
            catch (Exception e)
            {
                Log($"ERROR - Exception in RainWorldGame.RawUpdate IL hook: {e}");
            }
        });
    }
    
    // Hook: Adjust speedrun timer to remove changes from Cordyceps time control.
    public static double MoreSlugcats_SpeedRunTimer_GetTimerTickIncrement_Hook(
        On.MoreSlugcats.SpeedRunTimer.orig_GetTimerTickIncrement orig, RainWorldGame game, double dt)
    {
        var originalReturn = orig(game, dt);

        try
        {
            if (!CanAffectTickrate()) return originalReturn;
            
            var timeDialationFactor = game.framesPerSecond / (double) UnmodifiedTickrate;
            return originalReturn * timeDialationFactor;
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception in MoreSlugcats.SpeedRunTimer.GetTimerTickIncrement hook: {e}");
            return originalReturn;
        }
    }
    
    // Hook: Count ticks, handle tick pause.
    public static void RainWorldGame_Update_Hook(On.RainWorldGame.orig_Update orig, RainWorldGame self)
    {
        orig(self);

        try
        {
            if (Cordyceps2Settings.ShowTickCounter.Value && !TickCounterPaused && !self.GamePaused) TickCount++;
                
            if (!WaitingForTick) return;
                
            WaitingForTick = false;
            TickPauseOn = true;
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception in RainWorldGame.Update hook: {e}");
        }
    }
    
    public static bool CanAffectTickrate()
    {
        return TickrateCapOn || TickPauseOn;
    }
    
    private static void CheckInputs(float dt)
    {
        if (Input.GetKey(Cordyceps2Settings.ToggleInfoPanelKey.Value))
        {
            if (HeldKeys[0]) return;
            
            HeldKeys[0] = true;
            ShowInfoPanel = !ShowInfoPanel;
            InfoPanel.UpdateVisibility();
        }
        else HeldKeys[0] = false;

        if (Input.GetKey(Cordyceps2Settings.ResetTickCounterKey.Value))
        {
            if (HeldKeys[1]) return;
            
            HeldKeys[1] = true;
            TickCount = 0;
        }
        else HeldKeys[1] = false;

        if (Input.GetKey(Cordyceps2Settings.ToggleTickCounterPauseKey.Value))
        {
            if (HeldKeys[2]) return;
            
            HeldKeys[2] = true;
            TickCounterPaused = !TickCounterPaused;
        }
        else HeldKeys[2] = false;

        if (Input.GetKey(Cordyceps2Settings.ToggleTickPauseKey.Value))
        {
            if (HeldKeys[3]) return;
            
            HeldKeys[3] = true;

            if (WaitingForTick) return;
            TickPauseOn = !TickPauseOn;
        }
        else HeldKeys[3] = false;

        // The tick advance function works as such: When the key is pressed, the "WaitingForTick" flag is set,
        // Cordyceps releases the tick pause, and the game proceeds as normal until the next call to
        // RainWorldGame.Update(), at which point a hook checks if WaitingForTick is set, pausing the game and
        // unsetting it if it is. Effectively, the game automatically controls the tick pause while waiting
        // for the next tick for you.
        if (Input.GetKey(Cordyceps2Settings.TickAdvanceKey.Value))
        {
            if (HeldKeys[4]) return;
            
            HeldKeys[4] = true;

            if (!TickPauseOn) return;
            WaitingForTick = true;
            TickPauseOn = false;
        }
        else HeldKeys[4] = false;

        if (Input.GetKey(Cordyceps2Settings.ToggleTickrateCapKey.Value))
        {
            if (HeldKeys[5]) return;
            
            HeldKeys[5] = true;
            TickrateCapOn = !TickrateCapOn;
        }
        else HeldKeys[5] = false;

        if (Input.GetKey(Cordyceps2Settings.IncreaseTickrateCapKey.Value))
        {
            if (HeldKeys[6])
            {
                _keyHoldStopwatch += dt;

                if (!(_keyHoldStopwatch >= TickrateChangeInitialTime + TickrateChangeHoldTickTime)) return;

                DesiredTickrate = Math.Min(DesiredTickrate + 1, 40);
                _keyHoldStopwatch -= TickrateChangeHoldTickTime;

                return;
            }

            HeldKeys[6] = true;
            DesiredTickrate = Math.Min(DesiredTickrate + 1, 40);
        }
        else HeldKeys[6] = false;

        if (Input.GetKey(Cordyceps2Settings.DecreaseTickrateCapKey.Value))
        {
            if (HeldKeys[7])
            {
                _keyHoldStopwatch += dt;

                if (!(_keyHoldStopwatch >= TickrateChangeInitialTime + TickrateChangeHoldTickTime)) return;
                
                DesiredTickrate = Math.Max(DesiredTickrate - 1, 1);
                _keyHoldStopwatch -= TickrateChangeHoldTickTime;
                
                return;
            }

            HeldKeys[7] = true;
            DesiredTickrate = Math.Max(DesiredTickrate - 1, 1);
        }
        else HeldKeys[7] = false;

        _keyHoldStopwatch = 0f;
    }
    
    private static void Log(string str) { Debug.Log($"[Cordyceps/TimeControl] {str}"); }
}