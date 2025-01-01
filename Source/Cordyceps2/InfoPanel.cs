using System.Text.RegularExpressions;
using UnityEngine;

namespace Cordyceps2;

// Implementation mostly copied from Alphappy's TAMacro.
public static class InfoPanel
{
    private static readonly Color TextColor = new(255, 215, 36);
    private const float TextAlpha = 0.5f;
    
    private static FLabel _header;
    private static FLabel _infoLabel;
    private static FLabel _infoLabelData;
    private static FContainer _container;
    private static float _lineHeight;
    private static Vector2 _originalGrabMousePosition;
    private static Vector2 _originalGrabAnchorPosition;
    private static bool _panelIsGrabbed;

    private static Vector2 _panelAnchor = new Vector2(100.5f, 700f);
    
    private static float HeaderHeight => (Regex.Matches(_header.text, "\n").Count + 1) * _lineHeight;
    private static float InfoLabelHeight => (Regex.Matches(_infoLabel.text, "\n").Count + 1) 
                                            * _lineHeight;
    private static Vector2 PanelBounds => new Vector2(280f, HeaderHeight + InfoLabelHeight);

    public static void Initialize()
    {
        _container = new FContainer();
        Futile.stage.AddChild(_container);
        _container.SetPosition(Vector2.zero);

        _header = new FLabel(RWCustom.Custom.GetFont(),
                $"Cordyceps v{Cordyceps2Init.PluginVersion}\nPress " +
                $"[{Cordyceps2Settings.ToggleInfoPanelKey.Value.ToString()}] to toggle visibility of this " +
                "panel.\n" + 
                "You can also click and drag it to change its position.\n")
        {
            isVisible = true,
            alpha = TextAlpha,
            color = TextColor,
            alignment = FLabelAlignment.Left
        };
        _container.AddChild(_header);

        _infoLabel = new FLabel(RWCustom.Custom.GetFont(), "")
        {
            isVisible = true,
            alpha = TextAlpha,
            color = TextColor,
            alignment = FLabelAlignment.Left
        };
        _container.AddChild(_infoLabel);

        _infoLabelData = new FLabel(RWCustom.Custom.GetFont(), "")
        {
            isVisible = true,
            alpha = TextAlpha,
            color = TextColor,
            alignment = FLabelAlignment.Left
        };
        _container.AddChild(_infoLabelData);

        _lineHeight = _header.FontLineHeight * _header.scale;

        Update();
        UpdatePosition();
    }

    public static void Update()
    {
        _infoLabel.text =
            "Base Tickrate:\n" +
            "Desired Tickrate:\n" +
            "Tickrate Cap:\n" +
            "Tick Pause:" +
            (Cordyceps2Settings.ShowTickCounter.Value ? "\nTick Count:" : "");
        
        _infoLabelData.text =
            $"{TimeControl.UnmodifiedTickrate}\n" +
            $"{TimeControl.DesiredTickrate}\n" +
            (TimeControl.TickrateCapOn ? "On" : "Off") + "\n" +
            (TimeControl.TickPauseOn ? "On" : "Off") +
            (Cordyceps2Settings.ShowTickCounter.Value ? $"\n{TimeControl.TickCount}" : "");
    }

    private static void UpdatePosition()
    {
        _header.SetPosition(_panelAnchor);
        _infoLabel.SetPosition(_panelAnchor - new Vector2(0f, HeaderHeight * 2f));
        _infoLabelData.SetPosition(_panelAnchor - new Vector2(-110f, HeaderHeight * 2f));
    }

    public static void UpdateVisibility()
    {
        _container.isVisible = TimeControl.ShowInfoPanel;
    }

    public static void Remove()
    {
        _container.RemoveFromContainer();
        _container.RemoveAllChildren();
        _container = null;
    }

    public static void CheckGrab()
    {
        if (_header is not { isVisible: true }) return;
        
        Vector2 mpos = Input.mousePosition;
        if (Input.GetMouseButton(0))
        {
            if (!_panelIsGrabbed
                && mpos.x >= _panelAnchor.x
                && mpos.x <= _panelAnchor.x + PanelBounds.x
                && mpos.y <= _panelAnchor.y
                && mpos.y >= _panelAnchor.y - PanelBounds.y)
            {
                _panelIsGrabbed = true;
                _originalGrabAnchorPosition = _panelAnchor;
                _originalGrabMousePosition = mpos;
            }

            if (!_panelIsGrabbed) return;
            
            _panelAnchor = _originalGrabAnchorPosition + mpos - _originalGrabMousePosition;
            // Text is crisper if forced into alignment like this.
            _panelAnchor.x = Mathf.Floor(_panelAnchor.x) + 0.5f;
            
            UpdatePosition();
        }
        else
        {
            _panelIsGrabbed = false;
        }
    }
    
    // Hook: Initializes info panel when camera is created.
    public static void RoomCamera_ctor_Hook(On.RoomCamera.orig_ctor orig, RoomCamera self, RainWorldGame game, 
        int cameraNumber)
    {
        orig(self, game, cameraNumber);
        Initialize();
    }

    // Hook: Clears info panel when camera clears itself.
    public static void RoomCamera_ClearAllSprites_Hook(On.RoomCamera.orig_ClearAllSprites orig, RoomCamera self)
    {
        Remove();
        orig(self);
    }

    // Hook: Handles moving and updating info panel.
    public static void RainWorldGame_GrafUpdate_Hook(On.RainWorldGame.orig_GrafUpdate orig, RainWorldGame self,
        float timeStacker)
    {
        orig(self, timeStacker);
        CheckGrab();
        Update();
    }
}