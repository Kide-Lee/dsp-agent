---
name: update-steam-mod
description: Safely update the IcarusII BepInEx mod in the Steam/Proton Dyson Sphere Program install from dist/pkg, with backup, SHA-256 verification, config-preservation and legacy-config migration checks. Use when asked to update/install/deploy the mod to Steam, or when testing whether the packaged build matches the installed game.
---

# Update IcarusII in the Steam/Proton DSP install

Use this when the user asks to **update the Steam mod**, deploy the packaged build, or verify the installed game is current.

The one-command path is:

```bash
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh
```

Always run `--dry-run` first when the installed state is unknown:

```bash
.dsh/skills/update-steam-mod/scripts/update-steam-mod.sh --dry-run
```

The script only copies four files, backs up the old ones, verifies hashes, and checks that the existing sidecar `config.json` still migrates with its key intact. It **never** writes the game config or sidecar data.

## What gets updated

From `dist/pkg/IcarusII/BepInEx/plugins/IcarusII/`:

| File | Purpose |
|------|---------|
| `IcarusII.dll` | BepInEx plugin |
| `IcarusII.pdb` | stack traces / debugging |
| `sidecar/icarus-ii-sidecar.exe` | Windows sidecar (Proton runs this) |
| `ui/icarusii-ui` | chat-window AssetBundle |

Destination: `<DSP>/BepInEx/plugins/IcarusII/`.

## Never touch

- `<DSP>/BepInEx/config/icarus-ii.cfg` — player settings
- `.../compatdata/1366540/pfx/drive_c/users/<user>/AppData/Roaming/icarus-ii/config.json` — keys/providers
- `.../icarus-ii/runtime.json` — handshake token
- `.../icarus-ii/agent/` and `.../sessions/` — history

Do **not** run `install.sh --uninstall` or replace the whole plugin directory. Preserve `.bak-*` and `.backup-*`.

## Before running

1. Ensure the packaged artifacts are current:

   ```bash
   ./scripts/build-mod.sh                 # if mod source changed
   ./scripts/build-sidecar.sh windows-x64 # if sidecar source changed
   ./scripts/build-ui-bundle.sh           # if prefab/UI changed; needs Unity editor
   ```

   `build-ui-bundle.sh` refuses while Unity owns the project. If Unity is open, build the bundle from the editor or close it first.

2. Quit DSP. The script refuses if `DSPGAME.exe` is running.
3. Confirm `BepInEx/core/BepInEx.dll` exists.
4. For Proton, the prefix should already contain the `winhttp` override; the script checks and warns but does not edit the registry.

## Manual fallback

If the script cannot be used:

```bash
DSP="$HOME/.local/share/Steam/steamapps/common/Dyson Sphere Program"
PLUGIN="$DSP/BepInEx/plugins/IcarusII"
SRC="$PWD/dist/pkg/IcarusII/BepInEx/plugins/IcarusII"
TS=$(date +%Y%m%d-%H%M%S)
mkdir -p "$PLUGIN/.backup-$TS/sidecar" "$PLUGIN/.backup-$TS/ui"
cp -a "$PLUGIN/IcarusII.dll" "$PLUGIN/IcarusII.pdb" "$PLUGIN/.backup-$TS/"
cp -a "$PLUGIN/sidecar/icarus-ii-sidecar.exe" "$PLUGIN/.backup-$TS/sidecar/"
cp -a "$PLUGIN/ui/icarusii-ui" "$PLUGIN/.backup-$TS/ui/"
cp -f "$SRC/IcarusII.dll" "$PLUGIN/IcarusII.dll"
cp -f "$SRC/IcarusII.pdb" "$PLUGIN/IcarusII.pdb"
cp -f "$SRC/sidecar/icarus-ii-sidecar.exe" "$PLUGIN/sidecar/icarus-ii-sidecar.exe"
cp -f "$SRC/ui/icarusii-ui" "$PLUGIN/ui/icarusii-ui"
sha256sum "$SRC/IcarusII.dll" "$PLUGIN/IcarusII.dll"
```

## Verification

- Every copied file's SHA-256 matches the source package. The script enforces this.
- Legacy sidecar config still loads: run the script's migration check, or:

  ```bash
  node --input-type=module - <<'NODE'
  import { pathToFileURL } from 'node:url';
  const { ConfigStore } = await import(pathToFileURL('sidecar/dist/config.js').href);
  const s = new ConfigStore({ path: '/tmp/config-copy.json', requireApiKey: true });
  console.log(s.view());
  NODE
  ```

  Expect `state: "ready"`, the old provider/model active, and `hasKey: true`. Do not print the key.
- Proton prefix `user.reg` has:

  ```
  [Software\\Wine\\AppDefaults\\DSPGAME.exe\\DllOverrides]
  "winhttp"="native,builtin"
  ```

- Launch the game and press `R`; the log is `<DSP>/BepInEx/LogOutput.log`.

## Rollback

Copy the four files from the newest `BepInEx/plugins/IcarusII/.backup-<timestamp>/` back into place. The sidecar/config data is untouched, so rollback does not lose keys or history.

## Common failures

| Symptom | Cause / fix |
|---------|-------------|
| `DSPGAME 正在运行` | Quit the game, then rerun. |
| hash mismatch | Copy was interrupted; rerun; check disk/permissions. |
| BepInEx not loading | Missing Proton `winhttp` override; add it while no Wine process is running. |
| UI still old | `ui/icarusii-ui` was not rebuilt after prefab changes; rebuild the bundle. |
| Old key missing | Check the migration output; the new sidecar should migrate `provider/model/apiKey` to `providers.deepseek`. Never delete `config.json`. |
| `wineserver` warning | Copying files is still safe; only registry edits need Wine stopped. |

## Notes

- Steam library may be outside `~/.local/share/Steam`; the script reads `libraryfolders.vdf`. Use `--game-dir` if detection fails.
- The app id is `1366540`; the Proton prefix is `steamapps/compatdata/1366540/pfx`.
- If the game runs natively on Windows, skip the Proton registry check and use `%APPDATA%\icarus-ii`.
