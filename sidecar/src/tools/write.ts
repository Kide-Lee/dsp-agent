/**
 * M4 写工具（risk 1/2）。
 *
 * 这些工具只在 mod 声明 `game.write.v1` 时注册。执行前 sidecar 会发
 * `confirm_request`，只有玩家在游戏内确认后才带一次性 token 发 `tool_call`；
 * mod 还会再校验 token，sidecar 被绕过也不能直接执行。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import type { JsonObject, ToolResultPayload } from '../protocol.js';
import type { GameToolHost } from './host.js';

export const CAPABILITY_GAME_WRITE = 'game.write.v1';

export function isWriteToolName(name: string): boolean {
  return (WRITE_TOOL_NAMES as readonly string[]).includes(name);
}

export const WRITE_TOOL_NAMES = [
  'game_focus_camera',
  'game_place_marker',
  'game_open_panel',
  'game_set_research',
  'game_undo_last_write',
] as const;

type ToolArgs = JsonObject;

function formatResult(result: ToolResultPayload): string {
  if (!result.ok) {
    return `[${result.error?.code ?? 'tool_failed'}] ${result.error?.message ?? '未知错误'}`;
  }
  const data = result.data;
  const display =
    data && typeof data === 'object' && typeof (data as JsonObject)['display'] === 'string'
      ? String((data as JsonObject)['display'])
      : '';
  let json: string;
  try {
    json = JSON.stringify(data);
  } catch {
    json = String(data);
  }
  return display ? `${display}\n${json}` : json;
}

function writeTool(
  host: GameToolHost,
  spec: {
    name: string;
    label: string;
    description: string;
    promptSnippet: string;
    promptGuidelines: string[];
    parameters: ReturnType<typeof Type.Object>;
    timeoutMs: number;
    risk: 0 | 1 | 2;
  },
): ToolDefinition {
  return {
    name: spec.name,
    label: spec.label,
    description: spec.description,
    promptSnippet: spec.promptSnippet,
    promptGuidelines: spec.promptGuidelines,
    parameters: spec.parameters,
    executionMode: 'sequential',
    async execute(_toolCallId, params, signal) {
      const result = await host.call(spec.name, (params ?? {}) as ToolArgs, {
        timeoutMs: spec.timeoutMs,
        signal,
        risk: spec.risk,
      });
      return {
        content: [{ type: 'text' as const, text: formatResult(result) }],
        details: result,
      };
    },
  };
}

export function createWriteTools(host: GameToolHost): ToolDefinition[] {
  return [
    writeTool(host, {
      name: 'game_focus_camera',
      label: '移动视角',
      description:
        '把镜头移动到指定行星/星系或建筑。写操作：执行前玩家必须在游戏里确认。' +
        'target 可以是行星 id、星系 id 或建筑 id；成功后返回聚焦目标描述。',
      promptSnippet: '移动镜头到指定目标（需要玩家确认）',
      promptGuidelines: [
        'Use game_focus_camera 当玩家明确要求“带我去看/镜头移到”某个星球或建筑时。目标优先写 planet:<id> / star:<id> / entity:<id>，不要只传裸数字。',
        '不要为了自己看图而调用；需要截图时用 game_capture_view。',
        '执行前 sidecar 会弹确认，玩家拒绝时不要重复尝试。',
      ],
      parameters: Type.Object({
        target: Type.Union([Type.String(), Type.Integer()], {
          description: '目标：planet:102 / star:1 / entity:34；纯数字会按行星→当前建筑→星系推断',
        }),
      }),
      timeoutMs: 10000,
      risk: 1,
    }),
    writeTool(host, {
      name: 'game_place_marker',
      label: '新建标记',
      description:
        '在当前行星或指定行星上新建一个地图标记。写操作：执行前玩家必须确认。' +
        '用 latitude/longitude 指定位置（可从 game_get_entity 的 location 读取），text 是标记文字。',
      promptSnippet: '在行星上新建标记（需要玩家确认）',
      promptGuidelines: [
        'Use game_place_marker 当玩家明确要求“在这里做个标记/标个点”时。',
        '坐标必须来自游戏数据或玩家给出；不要凭截图猜坐标。优先使用 game_get_entity 返回的 location.latitude/longitude。',
      ],
      parameters: Type.Object({
        planetId: Type.Optional(Type.Integer({ description: '行星 id；省略为当前行星' })),
        latitude: Type.Optional(Type.Number({ description: '纬度（-90..90）' })),
        longitude: Type.Optional(Type.Number({ description: '经度（-180..180）' })),
        altitude: Type.Optional(Type.Number({ description: '海拔（米），默认 0' })),
        position: Type.Optional(Type.Object({
          x: Type.Number(),
          y: Type.Number(),
          z: Type.Number(),
        }, { description: '旧版行星局部坐标，仅兼容用' })),
        text: Type.String({ description: '标记完整文字；地图简述只显示前 2 个字，详情保存完整名称' }),
        icon: Type.Optional(Type.Integer({ description: '标记图标 id，可选' })),
      }),
      timeoutMs: 10000,
      risk: 1,
    }),
    writeTool(host, {
      name: 'game_open_panel',
      label: '打开面板',
      description:
        '打开游戏内面板：inventory（背包）、tech（科技树）、starmap（星图）、' +
        'marker（标记）、mecha（机甲）、replicator（复制器）、statistics（统计）、blueprint（蓝图）。' +
        '写操作：执行前玩家必须确认。',
      promptSnippet: '打开游戏面板（需要玩家确认）',
      promptGuidelines: [
        'Use game_open_panel 当玩家明确要求打开某个游戏面板时。',
        '只支持工具描述里列出的面板名；不要编造其他面板。',
      ],
      parameters: Type.Object({
        panel: Type.String({
          description: 'inventory / tech / starmap / marker / mecha / replicator / statistics / blueprint',
        }),
      }),
      timeoutMs: 8000,
      risk: 1,
    }),
    writeTool(host, {
      name: 'game_set_research',
      label: '切换研究',
      description:
        '把当前研究目标切换为指定科技。写操作，风险 2：可能浪费当前科技已投入的研究进度，' +
        '执行前玩家必须确认。techId 可以是科技 id 或名称。',
      promptSnippet: '切换当前研究目标（高风险，需要确认）',
      promptGuidelines: [
        'Use game_set_research 当玩家明确要求切换研究目标时。',
        '调用前先用 game_get_research 或 kb_get 确认 techId；不确定就先问玩家。',
        '如果玩家拒绝确认，不要再次调用。',
      ],
      parameters: Type.Object({
        techId: Type.Union([Type.Integer(), Type.String()], {
          description: '科技 id 或名称',
        }),
      }),
      timeoutMs: 8000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_undo_last_write',
      label: '撤销上一步',
      description:
        '撤销最近一次由本 mod 执行的写操作（视角、标记、面板、研究）。' +
        '只保留一层撤销栈；没有可撤销操作时返回明确错误。',
      promptSnippet: '撤销上一步写操作',
      promptGuidelines: [
        'Use game_undo_last_write 当玩家说“撤销/恢复刚才那一步”时。',
        '撤销不是写操作确认流程的一部分；只撤销最近一次，不要连续撤销多次。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 8000,
      risk: 0,
    }),
  ];
}
