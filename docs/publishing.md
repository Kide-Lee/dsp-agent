# 发布指南（Thunderstore / Nexus）

面向玩家发布时的清单。打包用脚本，不要手工改 `dist/`。

## 一、打包

```bash
# 完整包（含 UI bundle）→ dist/IcarusII-<ver>.zip
scripts/package-thunderstore.sh

# 不含 UI bundle 的变体（不重分发游戏美术资源）→ dist/IcarusII-<ver>-nogameassets.zip
SKIP_UI=1 scripts/package-thunderstore.sh

# 手动安装包（Nexus / 网盘）→ dist/IcarusII-0.2.0-manual.zip
scripts/package.sh && (cd dist/pkg && zip -r ../../dist/IcarusII-<ver>-manual.zip IcarusII)
```

元数据源文件在 `packaging/thunderstore/`：

| 文件 | 说明 |
| --- | --- |
| `manifest.json` | `name` 只能用字母/数字/下划线；`version_number` 必须与 `Plugin.cs` 的 `PluginVersion` 一致 |
| `icon.png` | **必须 256×256**，否则 Thunderstore 直接拒收 |
| `README.md` | 玩家向说明（安装、Key、隐私、FAQ）。仓库根 README 是开发向的，别混用 |
| `CHANGELOG.md` | 每次发版追加一节 |

脚本会自检：manifest 字段、版本号、图标尺寸、`plugins/IcarusII/` 布局，失败即非零退出。

## 二、BepInEx 依赖：已声明 5.4.17

- Thunderstore 社区包是 `xiaoye97-BepInEx` **5.4.17**，`manifest.json` 已声明
  `dependencies: ["xiaoye97-BepInEx-5.4.17"]`，mod 管理器会自动装好。
- 插件**按 5.4.17 编译**（`scripts/fetch-deps.sh` 默认版本），否则 BepInEx 会打印
  `Plugin [Icarus II] targets a wrong version of BepInEx (5.4.23.5)`。
- 5.4.23.5 运行时也验证通过；不要因为 changelog 里那条
  `Fix for unity 2022.3.62f3` 就升级编译目标（那条只影响取不到
  `Application.isBatchMode` 的构建，本游戏不受影响）。
- 实测记录（2026-10-17）：DSP 0.10.30.3100 + BepInEx 5.4.17 → `Chainloader ready` →
  `Loading [Icarus II 0.2.0]` → sidecar 握手 `gameVersion=0.10.30.3100`。

## 三、上传

### Thunderstore（主站）

1. 注册/登录，Create Team（团队名会成为包前缀，例如 `YourName-IcarusII`）；
2. Upload Package → 选 `dist/IcarusII-0.2.0.zip`；
3. 首次上传后页面 URL 形如 `https://thunderstore.io/c/dyson-sphere-program/p/<Team>/IcarusII/`，
   把它回填到 `packaging/thunderstore/manifest.json` 的 `website_url`（或填 GitHub 仓库）；
4. 后续更新：递增 `version_number` → 重新打包 → 上传同一包名。

### Nexus（第二入口）

1. 在 [Dyson Sphere Program](https://www.nexusmods.com/dysonsphereprogram) 建 mod 页；
2. 上传 `dist/IcarusII-0.2.0-manual.zip`（手动安装包，含 `install.sh`）；
3. 描述里直接贴 Thunderstore 链接，不要用网盘做主分发。

### 中文社区

NGA 戴森球版 / 百度贴吧 / B站（演示视频 + 专栏）/ 小黑盒。
统一落到 Thunderstore 页面，避免多个网盘版本并存。

## 四、发布前检查

- [ ] `README.md` 里的 SHA256 与实际 `IcarusII.dll` / `icarus-ii-sidecar.exe` 一致
      （发版前重新算一遍，脚本暂不自动回填）
- [ ] 游戏版本兼容性已验证（当前 `0.10.30.3100`）
- [ ] 杀软误报：附 SHA256，必要时提交微软误报申诉
- [ ] UI bundle 是否包含游戏自带贴图 → 决定发完整包还是 `-nogameassets`
- [ ] 截图/GIF 上传到图床后填进 README（Thunderstore 不读本地图片）

## 五、可选：CI 自动发版

`tcli`（Thunderstore CLI）可在 GitHub Actions 里上传：

```bash
dotnet tool install -g tcli
tcli publish --token "$TCLI_AUTH_TOKEN" \
  --file dist/IcarusII-<ver>.zip \
  --repository https://thunderstore.io
```

配合 `SKIP_SIDECAR=1` 的缓存策略（sidecar 100 MB，别每次重新构建）。
