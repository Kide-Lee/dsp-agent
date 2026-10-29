# sidecar 配置（config.json）

sidecar 的运行期配置只有两个来源：

1. 启动参数：`--host`、`--port`、`--data-dir`、`--token`、`--agent`、`--log-level` 等。
2. `<data-dir>/config.json`：供应商、模型、思考/权限档位、蓝图目录与 Wiki 端点。

不再使用 `ICARUS_BLUEPRINT_DIR`、`ICARUS_WIKI_*_API`、`ICARUS_II_LOG_LEVEL` 等环境变量覆盖运行期配置。

## 文件位置

- Windows：`%APPDATA%\icarus-ii\config.json`
- Linux / Proton：对应 Wine 前缀里的 `AppData/Roaming/icarus-ii/config.json`
- 可用 `--data-dir` 改到其它目录；该目录同时存放会话、知识库与 runtime.json。

Linux 下文件权限为 `0600`，目录为 `0700`。

## 结构

```jsonc
{
  "providers": {
    "deepseek": {
      "apiKey": null,
      "baseUrl": null,
      "models": [
        { "id": "deepseek-v4-flash", "name": "DeepSeek V4 Flash" }
      ]
    }
  },
  "active": { "provider": "deepseek", "model": "deepseek-v4-flash" },
  "thinkingLevel": "high",
  "permissionLevel": "limited",
  "blueprintDir": null,
  "wiki": {
    "zhApi": null,
    "enApi": null
  }
}
```

字段说明：

| 字段 | 说明 |
| --- | --- |
| `providers` | 供应商列表；`apiKey` 只写不读，`baseUrl` 为空表示用供应商默认地址 |
| `active` | 当前供应商与模型；与 `provider`/`model` 老字段冗余 |
| `thinkingLevel` | `off` / `low` / `high` / `max`，默认 `high` |
| `permissionLevel` | `readonly` / `limited` / `takeover`，默认 `limited` |
| `blueprintDir` | 蓝图根目录；`null` 表示 `Documents/Dyson Sphere Program/Blueprint`。原生 Linux 跑 `connectOnly` 时用它指向 Proton 前缀里的蓝图目录 |
| `wiki.zhApi` / `wiki.enApi` | 中文/英文 Wiki 的 `api.php` 地址；`null` 表示内置默认值 |

`blueprintDir`、`wiki.*` 在 sidecar 启动时读取；修改后重启 sidecar 生效。
`config.set` 不会修改这些字段。

## 迁移

- **不预置模型**：没有 key 时 `providers[].models` 保持为空，`config.state` 也不会返回缓存模型；先在设置页填 key，再用「获取可用模型」或手动添加。
- 老的单条 `provider` / `model` / `apiKey` / `baseUrl` 会自动迁移成 `providers` 里的一条。
- 缺少 `input` 的旧模型条目会按 DeepSeek 预设补 `text,image`，避免升级后截图被静默丢弃。
- 损坏或结构非法的 `config.json` 按“未配置”处理，不会让 sidecar 启动失败。
