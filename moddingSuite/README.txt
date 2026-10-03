WARNO Modding Suite - self-contained build 1.5.1-zstd
====================================================

What's included
---------------
  moddingSuite.exe          ONE binary - GUI + all CLI tools.
  tools\quickbms_win\       QuickBMS helper used by edata extraction
  Bundled .NET 9 runtime + dependencies (no install required).

How to run (GUI)
----------------
Just double-click moddingSuite.exe. No .NET runtime install needed.

CLI subcommands (same .exe, first argument selects mode)
--------------------------------------------------------
  moddingSuite.exe atlas --warno-root <dir> --asset-path <p> --out-json <f> --cache-dir <d>
                         [--atlas-file <f>] [--fallback ambient|none] [--include-sibling-assets] [--verbose]
      Atlas/texture metadata dumper (used by Blender plugin)
      --atlas-file reads exactly that TextureSmall.atlas; --fallback none skips the
      Mods/ModData/base and Output atlases. A missing --atlas-file or a bad --fallback
      exits 3. The JSON reports atlas_source_kind.
  moddingSuite.exe gfx   --warno-root <dir> --asset-path <p> --out-json <f> --cache-dir <d>
                         [--gfx-root <dir>] [--verbose]
      GFX descriptor dumper (used by Blender plugin)
      --gfx-root replaces <warno-root>\Output\AllPlatforms\NDF\GFX as the folder holding
      Unit/Weapon/Depiction/DepictionResources.ndfbin (names matched case-insensitively);
      a missing folder or file exits 3. The JSON reports gfx_source and resource_map.
  moddingSuite.exe ess   <file.ess> [more.ess ...]
      ESS -> WAV batch converter (stereo-correct)
  moddingSuite.exe tgv   <file.tgv> [more.tgv ...]
  moddingSuite.exe tgv   --atlas --file <TextureSmall.atlas> --tgv-root <dir> --out <dir>
      TGV -> PNG batch converter (BC1/3/5/7); --atlas extracts all sub-textures.

If it doesn't open
------------------
1. Run diagnostic.bat from this folder.
2. After the failed launch, send these files to the developer:
     - diagnostic_dump_*.txt   (next to the .exe)
     - logs\logging_*.dat      (next to the .exe)
3. Antivirus may quarantine the .exe - whitelist the whole folder if so.