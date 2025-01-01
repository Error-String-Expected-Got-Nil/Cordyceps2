using System;
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
    
    private static void CheckInputs(float dt)
    {
        if (Input.GetKey(Cordyceps2Settings.ToggleInfoPanelKey.Value) && !HeldKeys[0])
        {
            HeldKeys[0] = true;
            ShowInfoPanel = !ShowInfoPanel;
            InfoPanel.UpdateVisibility();
        }
        else HeldKeys[0] = false;

        if (Input.GetKey(Cordyceps2Settings.ResetTickCounterKey.Value) && !HeldKeys[1])
        {
            HeldKeys[1] = true;
            TickCount = 0;
        }
        else HeldKeys[1] = false;

        if (Input.GetKey(Cordyceps2Settings.ToggleTickCounterPauseKey.Value) && !HeldKeys[2])
        {
            HeldKeys[2] = true;
            TickCounterPaused = !TickCounterPaused;
        }
        else HeldKeys[2] = false;

        if (Input.GetKey(Cordyceps2Settings.ToggleTickPauseKey.Value) && !HeldKeys[3])
        {
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
        if (Input.GetKey(Cordyceps2Settings.TickAdvanceKey.Value) && !HeldKeys[4])
        {
            HeldKeys[4] = true;

            if (!TickPauseOn) return;
            WaitingForTick = true;
            TickPauseOn = false;
        }
        else HeldKeys[4] = false;

        if (Input.GetKey(Cordyceps2Settings.ToggleTickrateCapKey.Value) && !HeldKeys[5])
        {
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
}