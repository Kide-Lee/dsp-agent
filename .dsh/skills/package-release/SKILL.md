---
name: package-release
description: Build and package the IcarusII release into dist/pkg (Steam/manual install) and optionally the Thunderstore zip, reusing existing artifacts where safe. Use when preparing a release or before deploying to Steam.
---

# Package an IcarusII release

```bash
.dsh/skills/package-release/scripts/package-release.sh                    # 完整包
.dsh/skills/package-release/scripts/package-release.sh --skip-sidecar     # sidecar 未变，省时间
.dsh/skills/package-release/scripts/package-release.sh --with-thunderstore
```

Output: `dist/pkg/IcarusII/` (layout used by `update-steam-mod` and `install.sh`), plus `dist/IcarusII-<version>.zip` with `--with-thunderstore`.

## What `package.sh` does

1. `scripts/build-mod.sh` → copies `IcarusII.dll` + `.pdb`.
2. Reuses `dist/icarus-ii-sidecar.exe` when it exists **and** `SKIP_SIDECAR=1`; otherwise runs `scripts/build-sidecar.sh windows-x64`.
3. Copies `dsp-game/Build/IcarusIIBundle/icarusii-ui` if present — a missing bundle only warns, the mod then falls back to the code-built UI.
4. Copies `scripts/install.sh`, the Thunderstore README, `docs/protocol-v1.md`, `LICENSE`, `THIRD-PARTY.md`.

## Flags

| Flag | Effect |
| --- | --- |
| `--skip-sidecar` | `SKIP_SIDECAR=1`: reuse the existing exe instead of rebuilding it |
| `--with-thunderstore` | also run `scripts/package-thunderstore.sh` (reuses this build) |
| `--skip-ui` | Thunderstore zip excludes `ui/icarusii-ui` (no game art redistribution) |
| `--skip-build` | pass `SKIP_BUILD=1` to the Thunderstore stage |

## Before packaging

- Run the `verify-changes` skill for whatever changed; do not package a failing tree.
- Prefab/UI changes need the `rebuild-ui-bundle` skill **first**, otherwise the package silently ships the old window.
- Version bumps live in `packaging/thunderstore/manifest.json` (and the csproj `ModVersion`); check them before a release.

## After packaging

- Deploy: `update-steam-mod` skill (`--dry-run` first). Quit the game beforehand.
- Thunderstore upload: see `docs/publishing.md`; never hand-edit `dist/`.
- The script prints the file list and total size; confirm the sidecar exe and the UI bundle are both present before releasing.
