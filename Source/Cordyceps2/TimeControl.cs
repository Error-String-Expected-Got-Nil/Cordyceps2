using System;
using System.Diagnostics;
using System.Threading;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Cordyceps2;

// Mostly handles time control functions, as the name implies, but also has general input handling for lack of a better
// place to put it.
public static class TimeControl
{
    public static int UnmodifiedTickrate = 40;
    public static int DesiredTickrate = 40;
    // The factor TimeControl is slowing the game down by relative to what it would be normally. Ex.: if the current
    // default tickrate is 30 due to being in the depths, and TimeControl is slowing the tickrate down to 20, then this
    // will be 20 / 30.
    // TODO: Reset when RainWorldGame is no longer main loop
    public static float ArtificialTimeFactor = 1.0f;
    
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
    
    // TODO: DEBUG
    private static float _lastTimeFactor = 1.0f;
    private static bool _wasPaused;
    
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
                // Interesting discovery during bugfixing for audio recording: There's actually about 17.5 milliseconds
                // between each RainWorldGame.RawUpdate, on average. Most have a gap ~16.67ms, but sometimes there's a
                // spike to over 100ms. Not sure if this is relevant. 
                
                UnmodifiedTickrate = game.framesPerSecond;

                CheckInputs(dt);

                // TODO: DEBUG
                if (Cordyceps2Settings.RecordAudio.Value && TickPauseOn && !_wasPaused)
                {
                    Log("DEBUG - Pausing audio at " +
                        $"time = {(double)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 1000.0 : 0.00}ms");
                    AudioListener.pause = true;
                    _wasPaused = true;
                }
                
                if (!CanAffectTickrate())
                {
                    ArtificialTimeFactor = 1.0f;
                    CheckUnpause();
                    return;
                }

                var targetTickrate = TickPauseOn ? 0 : Math.Min(DesiredTickrate, game.framesPerSecond);
                ArtificialTimeFactor = UnmodifiedTickrate == 0 ? 0.0f : targetTickrate / (float)UnmodifiedTickrate;
                game.framesPerSecond = targetTickrate;
                CheckUnpause();
            }
            catch (Exception e)
            {
                Log($"ERROR - Exception in RainWorldGame.RawUpdate IL hook: {e}");
            }
            finally
            {
                // TODO: DEBUG
                if (ArtificialTimeFactor != _lastTimeFactor)
                {
                    Log($"DEBUG - Time factor altered to {ArtificialTimeFactor} at " +
                        $"time = {(double)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 1000.0 : 0.00}ms");
                    _lastTimeFactor = ArtificialTimeFactor;
                }
            }

            // TODO: DEBUG
            return;

            void CheckUnpause()
            {
                if (!Cordyceps2Settings.RecordAudio.Value) return;
                if (!TickPauseOn && _wasPaused)
                {
                    Log("DEBUG - Running GrafUpdate and unpausing audio at " +
                        $"time = {(double)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 1000.0 : 0.00}ms");
                    game.GrafUpdate(game.myTimeStacker);
                    AudioListener.pause = false;
                    _wasPaused = false;
                }
            }
        });
    }

    // TODO: DEBUG
    // Handles a part of audio syncing during recording.
    public static void RainWorldGame_GrafUpdate_Hook(On.RainWorldGame.orig_GrafUpdate orig, RainWorldGame self, 
        float timeStacker)
    {
        orig(self, timeStacker);

        try
        {
            
        }
        catch (Exception e)
        {
            Log($"ERROR - Exception in RainWorldGame_GrafUpdate_Hook: {e}");
        }
    }
    
    // Hook: Adjust speedrun timer to remove changes from Cordyceps time control.
    public static double MoreSlugcats_SpeedRunTimer_GetTimerTickIncrement_Hook(
        On.MoreSlugcats.SpeedRunTimer.orig_GetTimerTickIncrement orig, RainWorldGame game, double dt)
    {
        var originalReturn = orig(game, dt);

        try
        {
            if (!CanAffectTickrate()) return originalReturn;
            
            return originalReturn * ArtificialTimeFactor;
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
                
            // TODO: DEBUG
            Log($"DEBUG - Finished waiting for next tick at raw = {Recording._audioCapture._debugSamplesRaw}; " +
                $"time = {(double)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 1000.0 : 0.00}ms");
            
            // TODO: Theory, wait to update time factor until after next audio read after waiting on tick
            
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
            
            // TODO: DEBUG
            Log($"DEBUG - Toggle tick pause hit at raw = {Recording._audioCapture._debugSamplesRaw}; " +
                $"time = {(double)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 1000.0 : 0.00}ms; " +
                $"toggled to '{(TickPauseOn ? "on" : "off")}'");
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
            
            // TODO: DEBUG
            Log($"DEBUG - Tick advance hit at raw = {Recording._audioCapture._debugSamplesRaw}; " +
                $"time = {(double)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 1000.0 : 0.00}ms");
            
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
    
    private static void Log(string str) { Debug.Log($"[Cordyceps2/TimeControl] {str}"); }
}