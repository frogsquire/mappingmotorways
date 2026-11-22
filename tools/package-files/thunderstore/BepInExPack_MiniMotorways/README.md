# BepInExPack for Mini Motorways

[BepInEx](https://github.com/BepInEx/BepInEx) 5.4.23.2 (x86, Mono), set up for Mini Motorways. Mod managers install this automatically when a Mini Motorways mod depends on it.

Mini Motorways' Unity build is stripped harder than most, so plain BepInEx doesn't run. This pack adds:

- **Unstripped Mono class libraries** (`BepInEx\unstripped_corlib`, Windows *unityjit* build for Unity 2022.3.62f2, MIT-licensed) and a `doorstop_config.ini` that loads them instead of the game's cut-down copies.
- **StrippedApiShim** (`BepInEx\patchers`): adds, in memory only, the few Unity methods that BepInEx calls and the game's build removed. No game file is changed.
- **`BepInEx.cfg`** with `[Chainloader] HideManagerGameObject = true`. The game destroys visible foreign GameObjects; without this, plugins never get updates.

## Manual install

Copy the contents of the `BepInExPack_MiniMotorways` folder into the game folder (next to `Mini Motorways.exe`) and start the game.



## Licences

BepInEx, HarmonyX, MonoMod, Mono.Cecil and the Mono class libraries: MIT. Unity Doorstop (`winhttp.dll`): LGPL-2.1, source at https://github.com/NeighTools/UnityDoorstop/tree/v4.3.0. See `THIRD-PARTY-NOTICES.txt` and `licenses/`. No files of the game itself are included.
