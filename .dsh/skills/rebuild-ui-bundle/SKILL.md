---
name: rebuild-ui-bundle
description: Rebuild the ChatWindow UI AssetBundle from the prefab, refresh the prefab snapshot, and compare tool-bar anchors before/after to catch layout drift. Use after editing prefab/UI layout, or when the in-game chat window looks wrong.
---

# Rebuild the IcarusII UI bundle

```bash
.dsh/skills/rebuild-ui-bundle/scripts/rebuild-ui-bundle.sh
.dsh/skills/rebuild-ui-bundle/scripts/rebuild-ui-bundle.sh --add-parts   # 先跑"只做加法"的移植工具
```

The script refuses to run while Unity owns the project, dumps the prefab before and after the build, and fails if the four toolbar anchors moved.

## Preconditions

- Unity `2022.3.62f3` with Windows Build Support installed, at `$HOME/Unity/Hub/Editor/<version>/Editor/Unity` (override with `UNITY=/path/to/Unity`).
- Unity editor **closed**: batch mode takes an exclusive lock. `Temp/UnityLockfile` present → the script stops.
- Restricted sandboxes may need GPU/full access, otherwise Unity fails with `Failed to initialize unity graphics`.
- Prefab edits happen first, through MCP for Unity. This skill only packages:

  1. edit `dsp-game/Assets/IcarusII/ChatWindow.prefab` via MCP for Unity;
  2. screenshot the window and check the result;
  3. save, then close Unity;
  4. run this script.

## Why the dump comparison

The prefab is binary — `git diff` only shows `Bin`, so a silently shifted toolbar looks like a harmless byte change. The script greps the dump for the four right-anchored groups and compares `pos`/`sd`:

| Node | Right anchor |
| --- | --- |
| `send-button` | `-20` |
| `permission-group` | `-100` |
| `thinking-group` | `-220` |
| `model-group` | `-340` |

Drift means something (editor autosave, a builder run, opening the prefab) rewrote `anchoredPosition`. Do **not** compensate at runtime: restore `mod/unity/Prefab/ChatWindow.prefab` and rebuild.

`--add-parts` intentionally changes structure, so the anchor comparison only reports and never fails.

## What it produces / checks

- `dsp-game/Build/IcarusIIBundle/icarusii-ui` — StandaloneWindows64 bundle, packed by `package.sh` / `update-steam-mod`.
- `mod/unity/Prefab/` snapshot refresh (done by `scripts/build-ui-bundle.sh`); the script then `cmp`s the working prefab against the snapshot.
- `.scratch/ui-dump/{before,after}.txt` and `{before,after}.log` — keep them when reporting drift.

## Notes

- Only the DLL is rebuilt by `build-mod.sh`; a prefab change on its own is invisible in game until this bundle is rebuilt.
- `scripts/build-ui-bundle.sh --add-parts` adds missing parts; the default never regenerates the whole window.
- After a successful build: `package-release`, then `update-steam-mod`, then check in game at 1080p and one non-1080p resolution.
