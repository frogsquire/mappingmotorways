# Mapping Motorways

This is a mod (really, a collection of small mods) that adds extra mapping capabilities to Mini Motorways, so you can better plan out your city and analyze how it's working.

## AI usage note

This mod was written largely (primarily) by Claude Code, though a human (the listed author) reviews and directs all code changes, and this readme is human-written. There's no way I would have had time to do this without Claude's assistance, even if I might technically be capable. If that icks you out to the point of not wanting to use it, I begrudge you not.

## License

Do whatever you want with this code. Don't violate Mini Motorways' or Steam's license. Don't use it to cheat at the game for high scores. You'll make me sad.

## What's in this repo

| Folder | What it is | Where it's installed in the game |
|---|---|---|
| `RouteViz/` | The core mod and routing code | `BepInEx\scripts\` (dev, hot-reloaded) or `BepInEx\plugins\RouteViz\` |
| `HotReload/` | Dev tool: loads mods from `BepInEx\scripts` and reloads them live when they're rebuilt | `BepInEx\plugins\HotReload\` |
| `StrippedApiShim/` | BepInEx preloader patcher that adds a few Unity methods this game's build strips out, which BepInEx needs | `BepInEx\patchers\` |

There are a lot of other things you need to develop this mod - see `.gitignore`. It's up to you to find and (de)compile these, and to install the tools you might need to do so. This repo will never contain Dinosaur Polo Club code, and you shouldn't redistribute their code without permission.

## RouteViz

- **Click a car** to show its route with nice animations. Click again to hide it.
- **Click a house or destination** to show every car driving to or from it. Click again to hide them.
- **Click a destination's pin** to show the car coming to fulfil it. Click again to hide it.
- Press **C** to clear all routes. Press **Alt+C** to hide or show the mod's UI, but without changing what you have selected.
- Settings: `BepInEx\config\motorways.routeviz.cfg` (width, arrow speed, gray amount, click radius, keys, sounds).

The mod is read-only, and never changes the simulation. It uses the game's pathfinder only to fill in the start of trips that were already under way when it loaded.

## Why it's built this way

This game's Unity build is stripped harder than most, so we had to use the following workarounds:

- **No Unity audio clips.** `AudioClip.Create` and `AudioSource.PlayOneShot` are gone, so sounds go through the game's own G-Audio player (`AudioPlayer.UI.PlaySample`). There's a synthesised fallback via Windows `PlaySound`.
- **No texture tiling on lines.** `LineRenderer.textureMode` and material texture offsets are gone, so routes are meshes built every frame.
- **Other missing members:** `Mathf.PI`, the three-argument `Mathf.Min`, `ColorUtility`, `Object.FindObjectsOfType` (returns nothing). Compiling against the game's own `Managed` DLLs catches these at build time.
- **Hidden objects only.** The game destroys foreign root GameObjects, so plugin hosts must be hidden (`hideFlags = HideAndDontSave`). That's why BepInEx needs `HideManagerGameObject = true`, and why HotReload is our own rather than BepInEx.Debug's ScriptEngine.

## Setting up the game (once)

1. Install **BepInEx 5.4.23.2 (x86)** into the game folder, using its bundled release `winhttp.dll`.
2. The game ships stripped `mscorlib`/`System*` DLLs. Copy the Windows *unityjit* corlibs from another Unity **2022.3.62f2** Mono game into `BepInEx\unstripped_corlib\`, and set `dll_search_path_override = BepInEx\unstripped_corlib` in `doorstop_config.ini`. (The unity.bepinex.dev corlib zips are the Linux variant and don't work.)
3. In `BepInEx\config\BepInEx.cfg`, set `[Chainloader] HideManagerGameObject = true`.
4. Build `StrippedApiShim` and `HotReload` (below), then start the game once.

Don't replace the game's `UnityEngine.CoreModule.dll` with an unstripped one, as you'll get crrashes.

## Building

Needs the .NET SDK (any recent version but we target `net472`). Each project references the game's DLLs and copies its output into the game when built.

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
