using Menu.Remix.MixedUI;
using UnityEngine;

namespace Cordyceps2;

public class Cordyceps2Settings : OptionInterface 
{
    private static readonly string[] H264Presets = ["ultrafast", "veryfast", "faster", "fast", "medium", "slow", 
        "slower", "veryslow", "placebo"];
    
    public static readonly Cordyceps2Settings Instance = new();

    // Time Control Page
    // First column
    public static Configurable<KeyCode> ToggleInfoPanelKey =
        Instance.config.Bind(nameof(ToggleInfoPanelKey), KeyCode.M, new ConfigurableInfo(
            "Press to toggle visibility of info panel."));

    public static Configurable<KeyCode> ToggleTickrateCapKey =
        Instance.config.Bind(nameof(ToggleTickrateCapKey), KeyCode.Comma, new ConfigurableInfo(
            "Press to toggle tickrate cap on/off."));

    public static Configurable<KeyCode> IncreaseTickrateCapKey =
        Instance.config.Bind(nameof(IncreaseTickrateCapKey), KeyCode.Equals, new ConfigurableInfo(
            "Press or hold to increase tickrate cap."));

    public static Configurable<KeyCode> DecreaseTickrateCapKey =
        Instance.config.Bind(nameof(DecreaseTickrateCapKey), KeyCode.Minus, new ConfigurableInfo(
            "Press or hold to decrease tickrate cap."));

    public static Configurable<KeyCode> ToggleTickPauseKey =
        Instance.config.Bind(nameof(ToggleTickPauseKey), KeyCode.Period, new ConfigurableInfo(
            "Press to pause game by stopping physics ticks."));

    public static Configurable<KeyCode> TickAdvanceKey =
        Instance.config.Bind(nameof(TickAdvanceKey), KeyCode.Slash, new ConfigurableInfo(
            "Press to advance a single game tick. Only works while tick pause is active. Any inputs " +
            "held when tick is advanced will be registered on the frame you advance to."));
    
    // Second column
    public static Configurable<KeyCode> ResetTickCounterKey =
        Instance.config.Bind(nameof(ResetTickCounterKey), KeyCode.Semicolon, new ConfigurableInfo(
            "Press to reset tick counter to 0."));

    public static Configurable<KeyCode> ToggleTickCounterPauseKey =
        Instance.config.Bind(nameof(ToggleTickCounterPauseKey), KeyCode.Quote, new ConfigurableInfo(
            "Press to toggle pausing or unpausing the tick counter."));
    
    public static Configurable<bool> ShowTickCounter =
        Instance.config.Bind(nameof(ShowTickCounter), true, new ConfigurableInfo(
            "Toggle whether the tick counter should be added to the info panel."));
    
    // Recording Page
    // First column
    public static Configurable<KeyCode> StartRecordingKey =
        Instance.config.Bind(nameof(StartRecordingKey), KeyCode.R, new ConfigurableInfo(
            "Press to start recording. See log file for results."));
        
    public static Configurable<KeyCode> StopRecordingKey =
        Instance.config.Bind(nameof(StopRecordingKey), KeyCode.T, new ConfigurableInfo(
            "Press to stop recording. See log file for results."));
    
    // Second column
    public static Configurable<bool> EnableRecording =
        Instance.config.Bind(nameof(EnableRecording), true, new ConfigurableInfo(
            "Uncheck this to disable recording features."));
    
    public static Configurable<int> RecordingFps =
        Instance.config.Bind(nameof(RecordingFps), 40, new ConfigurableInfo(
            "The frames per second value to record at.", 
            new ConfigAcceptableRange<int>(1, 300)));

    public static Configurable<string> RecordingOutputDirectory =
        Instance.config.Bind(nameof(RecordingOutputDirectory), "C:\\cordyceps2", new ConfigurableInfo(
            "Directory to save recorded videos to."));
    
    // Footer
    // First column
    public static Configurable<int> KeyframeInterval =
        Instance.config.Bind(nameof(KeyframeInterval), 120, new ConfigurableInfo(
            "h264 encoder keyframe interval. Encoder must produce a complete frame at least once every this " +
            "many encoded frames.", 
            new ConfigAcceptableRange<int>(1, 300)));
    
    public static Configurable<float> ConstantRateFactor =
        Instance.config.Bind(nameof(ConstantRateFactor), 23.0f, new ConfigurableInfo(
            "h264 encoder constant rate factor. Higher values have more compression.", 
            new ConfigAcceptableRange<float>(0.0f, 51.0f)));
    
    public static Configurable<string> EncoderPreset =
        Instance.config.Bind(nameof(EncoderPreset), "veryfast", new ConfigurableInfo(
            "h264 encoder preset. Faster options are less efficient but take less processing time. Be " +
            "careful when increasing this, you should probably leave it on the default.",
            new ConfigAcceptableList<string>(H264Presets)));
    
    

    public override void Initialize()
    {
        base.Initialize();

        Tabs = [new OpTab(this, "Time Control"), new OpTab(this, "Recording")];
        
        // Time Control
        Tabs[0].AddItems(new UIelement[]
        {
            // First column
            new OpLabel(10f, 575f, "Toggle Info Panel")
                { description = ToggleInfoPanelKey.info.description },
            new OpKeyBinder(ToggleInfoPanelKey, new Vector2(150f, 570f),
                new Vector2(120f, 30f)) { description = ToggleInfoPanelKey.info.description },

            new OpLabel(10f, 540f, "Toggle Tickrate Cap")
                { description = ToggleTickrateCapKey.info.description },
            new OpKeyBinder(ToggleTickrateCapKey, new Vector2(150f, 535f),
                new Vector2(120f, 30f)) { description = ToggleTickrateCapKey.info.description },

            new OpLabel(10f, 505f, "Increase Tickrate Cap")
                { description = IncreaseTickrateCapKey.info.description },
            new OpKeyBinder(IncreaseTickrateCapKey, new Vector2(150f, 500f),
                new Vector2(120f, 30f)) { description = IncreaseTickrateCapKey.info.description },

            new OpLabel(10f, 470f, "Decrease Tickrate Cap")
                { description = DecreaseTickrateCapKey.info.description },
            new OpKeyBinder(DecreaseTickrateCapKey, new Vector2(150f, 465f),
                new Vector2(120f, 30f)) { description = DecreaseTickrateCapKey.info.description },

            new OpLabel(10f, 435f, "Toggle Tick Pause")
                { description = ToggleTickPauseKey.info.description },
            new OpKeyBinder(ToggleTickPauseKey, new Vector2(150f, 430f),
                new Vector2(120f, 30f)) { description = ToggleTickPauseKey.info.description },

            new OpLabel(10f, 400f, "Tick Advance")
                { description = TickAdvanceKey.info.description },
            new OpKeyBinder(TickAdvanceKey, new Vector2(150f, 395f),
                new Vector2(120f, 30f)) { description = TickAdvanceKey.info.description },

            // Second column
            new OpLabel(300f, 575f, "Reset Tick Counter")
                { description = ResetTickCounterKey.info.description },
            new OpKeyBinder(ResetTickCounterKey, new Vector2(450f, 570f),
                new Vector2(120f, 30f)) { description = ResetTickCounterKey.info.description },

            new OpLabel(300f, 540f, "Toggle Tick Counter Pause")
                { description = ToggleTickPauseKey.info.description },
            new OpKeyBinder(ToggleTickCounterPauseKey, new Vector2(450f, 535f),
                new Vector2(120f, 30f)) { description = ToggleTickCounterPauseKey.info.description },

            new OpLabel(300f, 505f, "Show Tick Counter")
                { description = ShowTickCounter.info.description },
            new OpCheckBox(ShowTickCounter, new Vector2(450f, 500f))
                { description = ShowTickCounter.info.description },

            // Footer
            new OpLabelLong(new Vector2(10f, 350f), new Vector2(570f, 0f),
                "Please see the README included in the mod's directory for more detailed information on " +
                "the functions of this mod!")
        });
        
        // Recording
        Tabs[1].AddItems(new UIelement[]
        {
            // First column
            new OpLabel(10f, 575f, "Start Recording")
                {description = StartRecordingKey.info.description},
            new OpKeyBinder(StartRecordingKey, new Vector2(150f, 570f), 
                new Vector2(120f, 30f)) {description = StartRecordingKey.info.description},
                
            new OpLabel(10f, 540f, "Stop Recording")
                {description = StopRecordingKey.info.description},
            new OpKeyBinder(StopRecordingKey, new Vector2(150f, 535f), 
                new Vector2(120f, 30f)) {description = StopRecordingKey.info.description},
            
            // Second column
            new OpLabel(300f, 575f, "Enable Recording")
                {description = EnableRecording.info.description},
            new OpCheckBox(EnableRecording, new Vector2(450f, 570f))
                {description = EnableRecording.info.description},
            
            new OpLabel(300f, 540f, "Recording FPS") 
                {description = RecordingFps.info.description},
            new OpUpdown(RecordingFps, new Vector2(450f, 535f), 120)
                {description = RecordingFps.info.description},
            
            // Middle row
            new OpLabel(10f, 505f, "Output Directory")
                {description = RecordingOutputDirectory.info.description},
            // TODO: Directory selection text box
            
            // Encoder options footer
            new OpLabelLong(new Vector2(10f, 150f), new Vector2(570f, 0f), 
                "h264 encoder options. Controls certain factors about how recorded videos are encoded. If you " +
                "don't already know what these mean or how they work, you should probably leave them default."),
            
            // Footer first column
            new OpLabel(10f, 100f, "Keyframe Interval") 
                {description = KeyframeInterval.info.description},
            new OpUpdown(KeyframeInterval, new Vector2(150f, 95f), 120)
                {description = KeyframeInterval.info.description},
            
            new OpLabel(10f, 65f, "Constant Rate Factor") 
                {description = ConstantRateFactor.info.description},
            new OpUpdown(ConstantRateFactor, new Vector2(150f, 60f), 120)
                {description = ConstantRateFactor.info.description},
            
            new OpLabel(10f, 30f, "Encoder Preset")
                {description = EncoderPreset.info.description},
            new OpComboBox(EncoderPreset, new Vector2(150f, 25f), 120, H264Presets)
                {description = EncoderPreset.info.description},
        });
    }
}