using System;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;

namespace StarDriftEvolutionVR
{
    [BepInPlugin("stardriftevolutionvr", "Star Drift Evolution VR", "0.1.0")]
    [BepInProcess("StarDriftEvolution.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        private ConfigEntry<bool> startInVr;
        private ConfigEntry<float> resolutionScale;
        private ConfigEntry<float> worldScale;
        private ConfigEntry<KeyCode> toggleKey;
        private ConfigEntry<KeyCode> recenterKey;
        private StereoRenderer stereo;
        private OpenVr.CompositorError lastError;
        private bool loggedStereo;

        private void Awake()
        {
            startInVr = Config.Bind("VR", "StartInVR", true, "Attempt to start OpenVR when the game launches.");
            resolutionScale = Config.Bind("VR", "ResolutionScale", 1f, new ConfigDescription(
                "Multiplier for SteamVR's recommended eye width and height. Takes effect when VR starts.",
                new AcceptableValueRange<float>(0.25f, 2f)
            ));
            worldScale = Config.Bind("VR", "WorldUnitsPerMeter", 1f, new ConfigDescription(
                "Game units per meter of headset movement and eye separation.",
                new AcceptableValueRange<float>(0.1f, 10f)
            ));
            toggleKey = Config.Bind("Controls", "ToggleVR", KeyCode.F8, "Start or stop the headset view.");
            recenterKey = Config.Bind("Controls", "Recenter", KeyCode.F9, "Recenter headset position and yaw.");
        }

        private void Start()
        {
            if (startInVr.Value)
            {
                StartVr();
            }
            StartCoroutine(RenderFrames());
        }

        private void Update()
        {
            if (stereo != null && stereo.QuitRequested())
            {
                StopVr();
                Logger.LogInfo("SteamVR ended the headset session.");
                return;
            }

            if (Input.GetKeyDown(toggleKey.Value))
            {
                if (stereo == null)
                {
                    StartVr();
                }
                else
                {
                    StopVr();
                    Logger.LogInfo("VR stopped.");
                }
            }
            if (Input.GetKeyDown(recenterKey.Value))
            {
                stereo?.Recenter();
            }
        }

        private IEnumerator RenderFrames()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (true)
            {
                yield return endOfFrame;
                if (stereo != null)
                {
                    RenderGameCamera();
                }
            }
        }

        private void RenderGameCamera()
        {
            GameManager game = GameManager.game_manager;
            if (game == null || game.cam == null)
            {
                return;
            }
            Camera camera = game.cam.GetComponent<Camera>();
            if (camera == null || !camera.isActiveAndEnabled)
            {
                return;
            }

            try
            {
                OpenVr.CompositorError error = stereo.Render(camera, worldScale.Value, out bool submitted);
                if (submitted && !loggedStereo)
                {
                    Logger.LogInfo("Submitted both eye textures to OpenVR.");
                    loggedStereo = true;
                }

                switch (error)
                {
                    case OpenVr.CompositorError.InvalidTexture:
                    case OpenVr.CompositorError.TextureIsOnWrongDevice:
                    case OpenVr.CompositorError.TextureUsesUnsupportedFormat:
                        Logger.LogError("SteamVR rejected an eye texture: " + error);
                        StopVr();
                        break;
                    case OpenVr.CompositorError.None:
                    case OpenVr.CompositorError.DoNotHaveFocus:
                        break;
                    default:
                        if (error != lastError)
                        {
                            Logger.LogWarning("OpenVR compositor: " + error);
                        }
                        lastError = error;
                        break;
                }
            }
            catch (Exception error)
            {
                Logger.LogError("VR rendering stopped: " + error);
                StopVr();
            }
        }

        private void StartVr()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
            {
                Logger.LogWarning("VR requires Direct3D 11. Add -force-d3d11 to the Steam launch options.");
                return;
            }

            try
            {
                stereo = StereoRenderer.Start(resolutionScale.Value);
                lastError = OpenVr.CompositorError.None;
                loggedStereo = false;
                Logger.LogInfo("OpenVR started. Use the game's camera control to change views.");
            }
            catch (DllNotFoundException)
            {
                Logger.LogWarning("OpenVR DLL unavailable. Put SteamVR's bin/win32/openvr_api.dll next to StarDriftEvolution.exe, then restart the game.");
            }
            catch (Exception error)
            {
                StopVr();
                Logger.LogWarning("Could not start VR: " + error.Message + " Check SteamVR, then press the VR toggle key to retry.");
            }
        }

        private void StopVr()
        {
            StereoRenderer renderer = stereo;
            stereo = null;
            renderer?.Dispose();
        }

        private void OnDestroy() => StopVr();
    }
}
