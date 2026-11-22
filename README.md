# Mini Motorways mods

BepInEx mods for Mini Motorways (Steam, Windows). The main one is **RouteViz**, which shows where cars are going.

## What's in this repo

| Folder | What it is | Where it's installed in the game |
|---|---|---|
| `RouteViz/` | The mod: click cars, pins or buildings to see routes | `BepInEx\scripts\` (dev, hot-reloaded) or `BepInEx\plugins\RouteViz\` |
| `HotReload/` | Dev tool: loads mods from `BepInEx\scripts` and reloads them live when they're rebuilt | `BepInEx\plugins\HotReload\` |
| `StrippedApiShim/` | BepInEx preloader patcher that adds a few Unity methods this game's build strips out, which BepInEx needs | `BepInEx\patchers\` |

Everything else in the working folder (decompilers, decompiled game code, reference mods) is deliberately untracked; see `.gitignore`. The decompiled game source belongs to Dinosaur Polo Club and must not be committed or published.

## RouteViz

- **Click a car** to show its route in the car's colour: the road ahead, plus the part already driven, grayed out. Click again to hide it. A ring shows which car you're over. The car flashes, and the game's own UI sounds play (pitched up for on, down for off; a warning buzz for a parked car with no route).
- **Click a house or destination** to show every car driving to or from it. If any are shown, the click hides them instead.
- **Click a destination's pin** to show the car coming to fulfil it.
- **C** clears all routes. **Alt+C** hides or shows them all (selections are kept while hidden).
- Routes are car-width bands with a pale middle and moving arrows. Where routes share a lane, that stretch pulses through each car's colour.
- Settings: `BepInEx\config\motorways.routeviz.cfg` (width, arrow speed, gray amount, click radius, keys, sounds).

The mod is read-only: it never changes the simulation. It uses the game's pathfinder only to fill in the start of trips that were already under way when it loaded.

## Why it's built this way

This game's Unity build is stripped harder than most, which shapes the code:

- **No Unity audio clips.** `AudioClip.Create` and `AudioSource.PlayOneShot` are gone, so sounds go through the game's own G-Audio player (`AudioPlayer.UI.PlaySample`). Pass `isImportant: true` or they're dropped. There's a synthesised fallback via Windows `PlaySound`.
- **No texture tiling on lines.** `LineRenderer.textureMode` and material texture offsets are gone, so routes are meshes built every frame.
- **Other missing members:** `Mathf.PI`, the three-argument `Mathf.Min`, `ColorUtility`, `Object.FindObjectsOfType` (returns nothing). Compiling against the game's own `Managed` DLLs catches these at build time.
- **Hidden objects only.** The game destroys foreign root GameObjects, so plugin hosts must be hidden (`hideFlags = HideAndDontSave`). That's why BepInEx needs `HideManagerGameObject = true`, and why HotReload is our own rather than BepInEx.Debug's ScriptEngine.

## Setting up the game (once)

1. Install **BepInEx 5.4.23.2 (x86)** into the game folder, using its bundled release `winhttp.dll`.
2. The game ships stripped `mscorlib`/`System*` DLLs. Copy the Windows *unityjit* corlibs from another Unity **2022.3.62f2** Mono game into `BepInEx\unstripped_corlib\`, and set `dll_search_path_override = BepInEx\unstripped_corlib` in `doorstop_config.ini`. (The unity.bepinex.dev corlib zips are the Linux variant and don't work.)
3. In `BepInEx\config\BepInEx.cfg`, set `[Chainloader] HideManagerGameObject = true`.
4. Build `StrippedApiShim` and `HotReload` (below), then start the game once.

Don't replace the game's `UnityEngine.CoreModule.dll` with an unstripped one: it crashes.

## Building

Needs the .NET SDK (any recent version; projects target `net472`). Each project references the game's DLLs and copies its output into the game when built.

```
dotnet build -c Release StrippedApiShim   # -> BepInEx\patchers   (restart the game to apply)
dotnet build -c Release HotReload         # -> BepInEx\plugins    (restart the game to apply)
dotnet build -c Release RouteViz          # -> BepInEx\scripts    (reloads live in a running game)
```

The game path defaults to `D:\SteamLibrary\steamapps\common\Mini Motorways`. Override it with `-p:GameDir="C:\path\to\Mini Motorways"`.

To install RouteViz as a normal plugin instead of a hot-reloaded script, run `dotnet build -c Release RouteViz -p:DeployTo=plugins`, and delete `BepInEx\scripts\RouteViz.dll` so it doesn't load twice.

## Dev loop

With HotReload installed, rebuild RouteViz while the game runs. About a second later the new copy replaces the old (or press **F6**). Selections and trip histories carry over. Hot-reloaded mods must clean up in `OnDestroy`. Their stack traces have no line numbers, because this Mono can't load symbols from memory.

Logs: `BepInEx\LogOutput.log`, and the game's `%USERPROFILE%\AppData\LocalLow\Dinosaur Polo Club\Mini Motorways\Player.log`. Set `VerboseLogging = true` in the RouteViz config for click details and the list of game sound names.
