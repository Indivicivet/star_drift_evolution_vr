# Star Drift Evolution VR

A BepInEx 5 plugin for the Windows Steam build of Star Drift Evolution. The
headset follows the game's current camera, including **Hood View** and **3rd
Person**. SteamVR supplies the headset pose, eye separation, and field of view.
Use the game's existing camera and driving controls.

## Install

1. Open the game folder through Steam → **Manage** → **Browse local files**.
2. Extract [BepInEx 5 for Windows x86](https://github.com/BepInEx/BepInEx/releases)
   next to `StarDriftEvolution.exe`. The game is 32-bit even on 64-bit Windows.
3. Put `StarDriftEvolutionVR.dll` in `BepInEx/plugins`. Build it using the
   instructions below.
4. Copy `SteamVR/bin/win32/openvr_api.dll` from your SteamVR installation to
   the folder containing `StarDriftEvolution.exe`.
5. Start SteamVR with your headset connected, then start the game. If the
   plugin logs a graphics API warning, add `-force-d3d11` to the game's Steam
   launch options.

The desktop still shows the game, including its menus and screen-space HUD.
Use the desktop for those controls and readouts. The headset renders the 3D
scene from player one's camera and uses its own stereo projection. Camera
effects implemented as separate post-processing components appear on the
desktop only.

- **F8** starts or stops VR. It also retries initialization if SteamVR was
  unavailable at launch.
- **F9** recenters headset position and yaw. Look forward from your usual
  seated position when pressing it.
- If the desktop caps the headset frame rate, disable VSync or select a
  higher frame rate in the game's graphics settings.

The config file is
`BepInEx/config/stardriftevolutionvr.cfg`.
Set `StartInVR = false` to launch in desktop mode. Lower `ResolutionScale` to
reduce GPU load. `WorldUnitsPerMeter` scales both head movement and eye
separation in game space. The plugin writes initialization and rendering
errors to `BepInEx/LogOutput.log`.

## Build

Use the .NET SDK and a local copy of the game's Windows Mono build with
BepInEx 5. The project targets .NET Framework 3.5 and references the game,
Unity, and BepInEx assemblies from disk.

```powershell
dotnet build -c Release "-p:GameDirectory=C:/Program Files (x86)/Steam/steamapps/common/Star Drift Evolution"
```

Set `GameDirectory` to your Steam library path if it differs. If BepInEx is
extracted somewhere else, pass `"-p:BepInExDirectory=C:/path/to/BepInEx"`.
The plugin is `bin/Release/net35/StarDriftEvolutionVR.dll`. The game and
loader reference assemblies stay in their original locations. The plugin
uses the game's camera pose at the end of each frame and renders to its own
textures, so it needs no Harmony patches or asset changes.

## Verification

Tested on Steam build 20292626 (Unity 2017.4.38f1) with BepInEx 5.4.23.6.
SteamVR accepted both eye textures with simulated headset tracking. The game
switched between Hood View and 3rd Person while the VR camera followed it.
Physical headset testing is pending.
