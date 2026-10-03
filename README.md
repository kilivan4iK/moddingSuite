Wargame modding suite  [![Donate](https://www.paypalobjects.com/en_US/i/btn/btn_donate_SM.gif "Shut up and take my money!")](https://www.paypal.com/cgi-bin/webscr?cmd=_s-xclick&hosted_button_id=XP3SV7QWZ9UEG "Shut up and take my money!")
============

This software brings modding to R.U.S.E, Wargame: European Escalation, Wargame: Airland Battle and Wargame: Red Dragon.

System Requirements
============

You need to have the .NET Desktop Runtime you can download it here:
https://dotnet.microsoft.com/download/dotnet/5.0

License
============

The Wargame modding suite is released under the MIT license.



**Simple guide**:
https://docs.google.com/document/d/1l2vut_I3q-TI-VGy777rl_Fw9RP6Ook9hjOF8qIyZXM/edit?usp=sharing 

Command line
============

`moddingSuite.exe <subcommand> ...` runs headless when the first argument is `atlas`, `gfx`, `ess` or `tgv` (case-insensitive); anything else opens the GUI. The CLI attaches to the parent console and writes stdout and stderr as **UTF-8 without BOM**. Results go to stdout. Diagnostics and errors go to stderr.

Option rules for `atlas` and `gfx`:

- Options are `--key value` pairs. The only bare flags are `--verbose` and, for `atlas`, `--include-sibling-assets`. Any option added later will also be a `--key value` pair.
- An unknown `--key value` pair is ignored, so a newer caller works with an older CLI. A bare unknown word fails with `Unknown argument: X` and exit code 3.
- An older CLI ignores options it does not know, such as `--atlas-file`, `--fallback` and `--gfx-root`. To check that the CLI honoured them, look at the output JSON. For `atlas`, check `atlas_source_kind == "atlas_file"`; an older CLI does not write `atlas_source_kind` at all. Do not compare `atlas_source` with the `--atlas-file` argument as raw strings: the CLI writes the absolute path from `Path.GetFullPath`, so a relative path or forward slashes on Windows never match. If you must compare paths, normalise both first, for example with `os.path.normcase(os.path.abspath(...))`. For `gfx`, check that `gfx_source` is present.
- `--out-json` is a file when it ends in `.json`. Otherwise it is a directory, and the CLI writes `atlas_map.json` or `gfx_manifest.json` into it.
- Pass `--asset-path` as `Assets/...` with forward slashes. `atlas` also strips a `GameData:/` prefix, but `gfx` does not.
- Exit codes: `0` ok, `2` no entries (`atlas` only), `3` error. With exit 2, stderr holds `atlas_cli_no_entries: ...`. With exit 3, stderr holds one of: an argument message (for example `Unknown argument: X`), `WARNO root not found: ...`, `Atlas not found for asset: ...` (`atlas`, when no candidate atlas exists; the most likely failure with `--fallback none`), `atlas_cli_error: ...` or `gfx_cli_error: ...`. The `Atlas not found for asset:` message has no `atlas_cli_` prefix.

### `atlas`: texture crop map for one model

```
moddingSuite.exe atlas --warno-root DIR --asset-path Assets/.../Model.fbx --out-json FILE --cache-dir DIR
                       [--atlas-file PATH] [--fallback ambient|none] [--verbose] [--include-sibling-assets]
```

| Option | Meaning |
|---|---|
| `--warno-root` | Game install folder. Must exist. |
| `--cache-dir` | Caller-owned folder. `<cache-dir>/PC/Atlas/<assetDir>/TextureSmall.atlas` is the first atlas candidate. |
| `--atlas-file PATH` | Read exactly this `TextureSmall.atlas` and search no other candidate. If the file is missing: `atlas_cli_error: --atlas-file not found: PATH`, exit 3. |
| `--fallback ambient\|none` | `ambient` (default): after the cache dir, also try `<warno-root>/Mods/ModData/base/PC/Atlas/...` and then `<warno-root>/Output/PC/Atlas/...`. These folders can hold atlases from before a game patch. `none`: use only the cache dir. Any other value gives exit 3. |
| `--include-sibling-assets` | Also include atlas parts of other models in the same asset folder. |
| `--verbose` | Print `atlas_candidate=` and `atlas_source=` lines to stderr. |

The output JSON has `schema_version` (1), `asset_path`, `included_asset_paths`, `atlas_source`, `generated_at`, `cli_version` and `textures[]`. Builds that support `--atlas-file` always also write `atlas_source_kind`: `atlas_file`, `cache`, `mods_base` or `output`. Older builds do not write it. Each texture has `source_tgv_rel`, `source_role`, `source_asset_path`, `crop_rect_px {x,y,w,h}` and `targets[] {logical_rel, basename, channel}`. When the page size is known and `crop_rect_px` runs past the page edge by exactly 1 px (rounding of `AbsPos`/`SizeInPixels`), the CLI trims that pixel. A larger overshoot is written unchanged, because it means the page size disagrees with the texture.

### `gfx`: unit, turret and depiction manifest for one model

```
moddingSuite.exe gfx --warno-root DIR --asset-path Assets/.../Model.fbx --out-json FILE --cache-dir DIR
                     [--gfx-root DIR] [--verbose]
```

| Option | Meaning |
|---|---|
| `--warno-root` | Game install folder. Must exist. It is also searched for the text depiction resources (`Mods/ModData/base`, `ModData/base`, `base` or the root itself, then `GameData/Gameplay/Gfx/DepictionResources`). They are the only source of descriptor-to-FBX paths: the decompiled `DepictionResources.ndfbin` does not keep the `Modele_*` names. |
| `--gfx-root DIR` | Folder holding `Unit.ndfbin`, `Weapon.ndfbin`, `Depiction.ndfbin` and `DepictionResources.ndfbin`. File names are matched case-insensitively. It replaces `<warno-root>/Output/AllPlatforms/NDF/GFX` entirely. If a file is missing, the CLI exits with 3 and names the file. |
| `--cache-dir` | Holds the shared caches (see below). |
| `--verbose` | Rebuild the index without reading or writing the index cache. Write the decompiled scripts to `<cache-dir>/_gfx_debug/*.ndf.txt`, and print `gfx_source=` and `gfx_resource_map entries=` lines to stderr. Only this mode decompiles `DepictionResources.ndfbin`. |

The output JSON has `schema_version` (2), `asset_path`, `source_files`, `matched_units[]`, `reference_meshes[]`, `operators`, `turrets[]`, `weapon_fx_anchors`, `subdepictions[]`, `track_kind`, `semantic_nodes[]`, `transform_debug[]`, `generated_at` and `cli_version`. Newer builds also add `gfx_source {kind: gfx_root|output_dump, dir}` and `resource_map {source, entries}`. `resource_map.entries` counts the descriptor-to-FBX entries read from the text `.ndf` files (`source` is `legacy_text`); with 0 entries no unit can match. Zero matched units is still exit 0.

Caches in `<cache-dir>/_gfx_shared/` can be deleted at any time; the CLI rebuilds them:

- `gfx_index.v<N>.<signature>.json`: the index, which does not depend on the asset. The signature covers each GFX source file's path, size and modification time, the GFX source folder, the CLI build (module version id) and the signature of the text `.ndf` files.
- `legacy_depiction_resources_map.v2.json`: the map from text `.ndf` files. The CLI reuses it only while the path, size and modification time of every source `.ndf` still match.
- Decompiled-script caches, keyed on the source `.ndfbin` path, size and modification time and on the CLI build.

The CLI also keeps an NDF name index in `%LOCALAPPDATA%\moddingSuite\ndf_name_index\`, outside `--cache-dir`.

`cli_version` in both JSONs is `<assembly version>+<first 8 hex chars of the module version id>`, for example `1.0.0.0+1a2b3c4d`. The suffix changes with every build, so consumers can tell builds apart.

### `ess` and `tgv`

```
moddingSuite.exe ess <file.ess> [more.ess ...]        decode each to a sibling .wav
moddingSuite.exe tgv <file.tgv> [more.tgv ...]        decode each to a sibling .png (full-res mip)
moddingSuite.exe tgv --atlas --file <TextureSmall.atlas> --tgv-root DIR --out DIR
                                                      cut every atlas part out of its source TGV
```

In `tgv --atlas` mode, `--atlas` must come first. The CLI finds each source TGV under `--tgv-root` by path-tail or file-name match.

Exit codes for `ess` and `tgv`: `0` ok, `1` usage error (for `tgv --atlas`, also a missing atlas or TGV root), `2` at least one file failed (`tgv --atlas`: nothing was written).
