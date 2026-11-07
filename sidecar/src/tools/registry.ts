/**
 * M2 只读游戏工具定义（协议附录 B）。
 *
 * 这些工具只声明 schema 与调用方式；真正的采集在 mod 侧。
 * 工具名使用下划线：OpenAI 兼容端点会把非 [a-zA-Z0-9_-] 字符替换成 `_`，
 * 用点号会导致模型看到的工具名与回传的 tool_call.name 不一致。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import type { ExtensionSummary, ExtensionTool, JsonObject, ToolResultPayload } from '../protocol.js';
import { GameToolHost, registerGameToolsFactory, SCRIPT_TOOL_NAME, toolError } from './host.js';
import { WIKI_TOOL_NAMES } from '../wiki/tools.js';
import { BLUEPRINT_TOOL_NAMES } from '../blueprint/tools.js';
import { KB_TOOL_NAMES } from '../kb/tools.js';
import { CAPABILITY_UI_ASK, UI_ASK_TOOL_NAME, UI_ASK_TOOL_NAME_ALIASES } from './ask.js';
import type { ToolGroup } from '../protocol.js';
import {
  CAPABILITY_GAME_BUILD,
  CAPABILITY_GAME_DROP,
  CAPABILITY_GAME_STORAGE,
  CAPABILITY_GAME_TETHER,
  CAPABILITY_GAME_TODO,
  CAPABILITY_GAME_TODO_WRITE,
  CAPABILITY_GAME_WRITE,
  WRITE_TOOL_NAMES,
  createWriteTools,
  isWriteToolName,
  writeToolRisk,
} from './write.js';

export const CAPABILITY_GAME_READ = 'game.read.v1';
export const CAPABILITY_GAME_ENTITY = 'game.entity.v1';
export const CAPABILITY_GAME_PROGRESS = 'game.progress.v1';
export const CAPABILITY_GAME_VIEW = 'game.view.v1';
export const CAPABILITY_GAME_ANALYZE = 'game.analyze.v1';
/** 第三方 mod 桥（P0）：插件列表与配置读写。 */
export const CAPABILITY_MOD_BRIDGE = 'mod.bridge.v1';
/** uGUI 控件树（P3）：快照与合成点击。 */
export const CAPABILITY_UI_TREE = 'ui.tree.v1';
/** 空间变量查询（P0）：区域 / 定点 / 动点。 */
export const CAPABILITY_SPATIAL = 'space.read.v1';

/** 桥工具名；测试用来断言能力门禁。 */
export const BRIDGE_TOOL_NAMES = [
  'mods_list',
  'mods_config_list',
  'mods_config_get',
  'mods_config_set',
  'mods_api_list',
  'mods_adapter_create',
  'mods_adapter_delete',
] as const;

export const GAME_TOOL_NAMES = [
  'game_get_overview',
  'game_get_player',
  'game_get_planet',
  'game_get_power',
  'game_get_research',
  'game_get_production',
] as const;

export const ENTITY_TOOL_NAMES = ['game_get_entity', 'game_get_entities'] as const;
export const PROGRESS_TOOL_NAMES = ['game_get_progress'] as const;
export const VIEW_TOOL_NAMES = ['game_capture_view'] as const;
export const SPATIAL_TOOL_NAMES = ['space_region', 'space_point', 'space_motion'] as const;

/** 工具名 → 所需能力。新增工具必须在这里登记，否则不会被广告。 */
const TOOL_CAPABILITY: Record<string, string> = {
  game_get_overview: CAPABILITY_GAME_READ,
  game_get_player: CAPABILITY_GAME_READ,
  game_get_planet: CAPABILITY_GAME_READ,
  game_get_power: CAPABILITY_GAME_READ,
  game_get_research: CAPABILITY_GAME_READ,
  game_get_production: CAPABILITY_GAME_READ,
  game_get_entity: CAPABILITY_GAME_ENTITY,
  game_get_entities: CAPABILITY_GAME_ENTITY,
  game_get_progress: CAPABILITY_GAME_PROGRESS,
  game_capture_view: CAPABILITY_GAME_VIEW,
  game_analyze_area: CAPABILITY_GAME_ANALYZE,
  game_render_layout: CAPABILITY_GAME_ENTITY,
  game_copy_blueprint: CAPABILITY_GAME_ENTITY,
  game_get_planets: CAPABILITY_GAME_ENTITY,
  game_get_logistics: CAPABILITY_GAME_ENTITY,
  game_trace_line: CAPABILITY_GAME_ENTITY,
  game_place_marker: CAPABILITY_GAME_WRITE,
  game_open_panel: CAPABILITY_GAME_WRITE,
  game_set_research: CAPABILITY_GAME_WRITE,
  game_drop_item: CAPABILITY_GAME_DROP,
  game_set_recipe: CAPABILITY_GAME_BUILD,
  game_set_assembler_batch: CAPABILITY_GAME_BUILD,
  game_take_output: CAPABILITY_GAME_BUILD,
  game_storage_set_filter: CAPABILITY_GAME_STORAGE,
  game_storage_set_limit: CAPABILITY_GAME_STORAGE,
  game_storage_move: CAPABILITY_GAME_STORAGE,
  game_storage_deposit: CAPABILITY_GAME_STORAGE,
  game_storage_withdraw: CAPABILITY_GAME_STORAGE,
  game_feed_input: CAPABILITY_GAME_BUILD,
  game_tether_add: CAPABILITY_GAME_TETHER,
  game_tether_clear: CAPABILITY_GAME_TETHER,
  game_tether_list: CAPABILITY_GAME_TETHER,
  game_todo_templates: CAPABILITY_GAME_TODO,
  game_todo_list: CAPABILITY_GAME_TODO,
  game_todo_add: CAPABILITY_GAME_TODO,
  game_todo_remove: CAPABILITY_GAME_TODO,
  game_todo_write: CAPABILITY_GAME_TODO_WRITE,
  mods_list: CAPABILITY_MOD_BRIDGE,
  mods_api_list: CAPABILITY_MOD_BRIDGE,
  mods_adapter_create: CAPABILITY_MOD_BRIDGE,
  mods_adapter_delete: CAPABILITY_MOD_BRIDGE,
  ui_snapshot: CAPABILITY_UI_TREE,
  ui_click: CAPABILITY_UI_TREE,
  game_undo_last_write: CAPABILITY_GAME_WRITE,
  space_region: CAPABILITY_SPATIAL,
  space_point: CAPABILITY_SPATIAL,
  space_motion: CAPABILITY_SPATIAL,
};

const MAX_RESULT_CHARS = 9000;

type ToolArgs = JsonObject;

/** pi 的工具结果内容块：既有文本也有图片（协议附录 B.3 的 `image`）。 */
type ToolContentBlock =
  | { type: 'text'; text: string }
  | { type: 'image'; mimeType: string; data: string };

function isObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function formatToolResult(result: ToolResultPayload): string {
  if (!result.ok) {
    const code = result.error?.code ?? 'tool_failed';
    const message = result.error?.message ?? '未知错误';
    return `[${code}] ${message}`;
  }

  const data = result.data;
  const display = isObject(data) && typeof data['display'] === 'string' ? data['display'] : '';
  let json: string;
  try {
    json = JSON.stringify(data);
  } catch {
    json = String(data);
  }

  const meta = result.meta ?? {};
  const notes: string[] = [];
  if (typeof meta['gameTick'] === 'number') notes.push(`gameTick=${meta['gameTick']}`);
  if (typeof meta['saveKey'] === 'string') notes.push(`save=${meta['saveKey']}`);
  if (meta['truncated'] === true) notes.push('结果已截断，可用 offset 继续');

  let text = `${display ? `${display}\n` : ''}${json}`;
  if (notes.length > 0) text += `\n(${notes.join(', ')})`;
  if (text.length > MAX_RESULT_CHARS) {
    text = `${text.slice(0, MAX_RESULT_CHARS)}\n…（结果过长，已截断；请用 offset/limit 缩小范围）`;
  }
  return text;
}

/**
 * 把一次工具结果转成 pi 的内容块。
 *
 * 图片单独成块，不混进文本：pi 的 provider 适配层会把它转成真正的图像输入
 * （`openai-completions` 会补一条带 `image_url` 的 user 消息）。模型不支持看图时
 * 那个适配层会丢弃图片，文本里的 `截图` 提示仍然保留。
 */
function buildToolContent(result: ToolResultPayload): ToolContentBlock[] {
  const content: ToolContentBlock[] = [{ type: 'text', text: formatToolResult(result) }];
  const image = result.ok ? result.image : null;
  if (image && image.data.length > 0) {
    content.push({ type: 'image', mimeType: image.mimeType, data: image.data });
  }
  return content;
}

function gameTool(
  host: GameToolHost,
  spec: {
    name: string;
    label: string;
    description: string;
    promptGuidelines?: string[];
    parameters: ReturnType<typeof Type.Object>;
    timeoutMs: number;
    risk?: number;
  },
): ToolDefinition {
  return {
    name: spec.name,
    label: spec.label,
    description: spec.description,
    promptGuidelines: spec.promptGuidelines,
    parameters: spec.parameters,
    executionMode: 'sequential',
    async execute(_toolCallId, params, signal) {
      const result = await host.call(spec.name, (params ?? {}) as ToolArgs, {
        timeoutMs: spec.timeoutMs,
        signal,
        risk: spec.risk ?? 0,
      });
      return {
        content: buildToolContent(result),
        details: result,
      };
    },
  };
}

export function createGameTools(host: GameToolHost): ToolDefinition[] {
  const planetArg = Type.Optional(
    Type.Integer({ description: '行星 id；省略时使用当前所在行星' }),
  );
  const offsetArg = Type.Optional(Type.Integer({ description: '分页偏移，默认 0' }));
  const limitArg = Type.Optional(Type.Integer({ description: '返回条数上限，默认 25' }));

  const tools: ToolDefinition[] = [
    gameTool(host, {
      name: 'game_get_overview',
      label: '存档概览',
      description:
        '读取当前存档概览：存档名、游戏时刻、当前行星/星系、玩家经纬度、是否暂停。' +
        '玩家问“我在哪”“现在什么情况”时先调用它；不要凭印象回答。',
      promptGuidelines: [
        '玩家询问当前存档、所在星球、游戏时间或“现在什么情况”时使用。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 5000,
    }),
    gameTool(host, {
      name: 'game_get_player',
      label: '玩家状态',
      description:
        '读取伊卡洛斯的能量、护盾、反应堆能量与背包物品（id、名称、数量）。' +
        '玩家问“我还有多少能量”“背包里有什么”时调用。',
      promptGuidelines: [
        '玩家询问自己的能量、护盾或背包物品时使用。',
      ],
      parameters: Type.Object({
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 6000,
    }),
    gameTool(host, {
      name: 'game_get_planet',
      label: '行星状态',
      description:
        '读取指定行星（省略则为当前行星）的类型、半径、矿脉按资源汇总、建筑按原型计数。' +
        'veins=groups 返回按位置聚合的矿脉群（已扫描的远程行星也可）；veins=nodes 分页返回逐矿脉节点' +
        '（仅工厂已加载的行星）。远程行星未加载工厂时明确标注 factoryLoaded=false。',
      promptGuidelines: [
        '玩家询问某个星球有什么矿、矿在哪、建了多少建筑时使用。',
        '需要矿脉位置时用 veins="groups"；需要单个矿脉的精确经纬度时用 veins="nodes" 并配合 veinOffset/veinLimit。',
        '矿脉群/节点可能很多，不要一次拉全量。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        veins: Type.Optional(
          Type.String({
            description: 'summary（默认，按资源汇总）/ groups（矿脉群+位置）/ nodes（逐矿脉+位置，需工厂已加载）/ all',
          }),
        ),
        veinOffset: Type.Optional(Type.Integer({ description: '逐矿脉分页起点，默认 0' })),
        veinLimit: Type.Optional(Type.Integer({ description: '逐矿脉每页数量，默认 50，最大 200' })),
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 12000,
    }),
    gameTool(host, {
      name: 'game_get_power',
      label: '电力状态',
      description:
        '读取指定行星（省略则为当前行星）的电网：需求、已供、容量、储能、满足率与充放电。' +
        '所有功率单位为 kW，储能为 kWh；玩家问“电够不够”时调用。',
      promptGuidelines: [
        '玩家询问电力是否足够、为什么停电或电网负载时使用。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
      }),
      timeoutMs: 6000,
    }),
    gameTool(host, {
      name: 'game_get_research',
      label: '研究状态',
      description:
        '读取当前研究科技、进度、队列、研究速度与已解锁科技数量。' +
        '玩家问“现在研究什么”“还差多少”时调用。',
      promptGuidelines: [
        '玩家询问研究进度、科技队列或已解锁科技时使用。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 5000,
    }),
    gameTool(host, {
      name: 'game_get_production',
      label: '实际产出',
      description:
        '读取指定行星（省略则为当前行星）按物品聚合的产量/消耗/库存（每分钟口径），' +
        '按净产出从低到高排序，并给出库存还能撑几分钟。玩家问“我缺什么”时调用它，' +
        '再结合 game_get_power 判断瓶颈。',
      promptGuidelines: [
        '玩家问“我缺什么”、某条产线为什么停、库存还能撑多久时使用。',
        'basis=stats 是实际统计；basis=installed 表示 1 分钟桶为空，items 是按装配机算的装机产能（statsEmpty=true）。',
        'statsEmpty=true 时不要下“没有产线”的结论；设备在别处或需要空间明细时改调 game_analyze_area。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        itemFilter: Type.Optional(
          Type.String({ description: '按物品名或 id 过滤，例如“铁块”或“1001”' }),
        ),
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 12000,
    }),
    gameTool(host, {
      name: 'game_get_entity',
      label: '建筑详情',
      description:
        '按 entityId 读取单个建筑（或用 entityIds 批量读取）的实际情况，尽量对齐游戏面板：原型、配方、输入/输出槽（数量/需求/增产）、' +
        '功率与供电满足率、工作状态（缺料/没电/运行/待机）、分拣器目标、传送带线路容量/节点/货物数与区间、' +
        '仓储容量、物流塔槽位（当前/在途/上限/供需模式）、无人机/运输船/翘曲器与充电状态。' +
        'fields 可裁剪返回段（assembler/miner/inserter/belt/storage/station/components；默认全量）；' +
        '批量 entityIds 默认只返回 core，最多 10 个。' +
        'id 来自 game_get_entities 或截图标注；id 会被回收，返回里的 stale=true 表示该 id 已被别的建筑占用。',
      promptGuidelines: [
        '玩家问某一栋建筑为什么不动、配方是什么、插了什么、有没有电时使用。',
        '先只取需要的段：例如只关心缺料时传 fields=["assembler"]；要逐字段调试才传 components。',
        '检查多栋建筑时用 entityIds（最多 10 个）并配合 fields，避免一次返回完整详情。',
        '如果返回 stale=true，不要使用这条数据：id 已被回收，重新用 game_get_entities 定位。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        entityId: Type.Optional(Type.Integer({ description: '单个建筑 id（PlanetFactory.entityPool 的下标）' })),
        entityIds: Type.Optional(
          Type.Array(Type.Integer(), { description: '批量建筑 id，最多 10 个；与 entityId 二选一' }),
        ),
        expectProtoId: Type.Optional(
          Type.Integer({ description: '期望的原型 id；不匹配时返回 stale=true' }),
        ),
        fields: Type.Optional(
          Type.Union([Type.String(), Type.Array(Type.String())], {
            description: '要返回的段：core/assembler/miner/inserter/belt/storage/station/components；也接受逗号分隔字符串',
          }),
        ),
      }),
      timeoutMs: 6000,
    }),
    gameTool(host, {
      name: 'game_get_entities',
      label: '建筑列表',
      description:
        '分页列出指定行星（省略则为当前行星）的建筑：id、原型、配方、星系/行星/经纬度、朝向。' +
        '可按原型 id/名称、配方 id 过滤；配合 game_get_entity 查看单体细节。' +
        '大星球请用 offset/limit 分页，不要一次拉全量。',
      promptGuidelines: [
        '需要“这片区域有哪些建筑”“某个配方有多少台机器”时使用。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        protoId: Type.Optional(Type.Integer({ description: '按建筑原型 id 过滤' })),
        protoName: Type.Optional(
          Type.String({ description: '按建筑原型名称子串过滤，例如“组装机”' }),
        ),
        recipeId: Type.Optional(Type.Integer({ description: '按配方 id 过滤' })),
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 10000,
    }),
    gameTool(host, {
      name: 'game_get_progress',
      label: '进度阶段',
      description:
        '读取玩家进度：派生阶段（电磁/炼油/钛/星际物流/翘曲/戴森球…）及判定依据、' +
        '已解锁科技数、里程碑、教程、当前研究与队列。' +
        '玩家问“我下一步该干什么”“我在什么阶段”时先调用它。',
      promptGuidelines: [
        '玩家询问当前进度、下一步该做什么、阶段目标时使用。',
        '回答“下一步”时只给当前阶段与下一阶段的必要条件，不要剧透更后面的科技细节。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 6000,
    }),
    gameTool(host, {
      name: 'game_capture_view',
      label: '截图',
      description:
        '截取当前游戏画面（不含 UI），以 JPEG 返回图片本身，并附带可见建筑的屏幕坐标。' +
        'view=topdown 临时切俯视看平面布局；view=orbit 绕目标点换一个角度；拍完自动还原玩家视角。' +
        '注意：只能拍到当前行星已渲染的区块，相机不会触发远处地形/建筑加载。',
      promptGuidelines: [
        '玩家让你看画面、问布局/摆放/接线是否正确时使用。',
        '需要平面布局时用 view=topdown，并用 entityId/center 指定中心、cameraHeight 指定高度（10–150m）。',
        '需要换角度时用 view=orbit，配合 entityId/center、distance、pitch、yaw；一次调用只拍一个角度，想看多个方向就分多次调用并改 yaw/pitch/distance。',
        '俯视图只覆盖玩家附近已加载的区块；画面缺一块时说明是流送限制，不要当成空地。',
        '图上的编号框与 legend 的 index 一一对应；先按编号定位，再结合 game_get_entity / game_get_entities 的数值下结论，不要只凭像素猜配方。',
        'annotate=false 可关掉标注（例如只想看纯净画面）。',
      ],
      parameters: Type.Object({
        width: Type.Optional(Type.Integer({ description: '像素宽，默认 1280，上限 1600' })),
        height: Type.Optional(Type.Integer({ description: '像素高，默认 720，上限 900' })),
        includeEntities: Type.Optional(
          Type.Boolean({ description: '是否附带可见建筑的屏幕坐标，默认 true' }),
        ),
        annotate: Type.Optional(
          Type.Boolean({ description: '是否在图上画编号框并返回 legend，默认 true' }),
        ),
        view: Type.Optional(
          Type.Union([
            Type.Literal('player'),
            Type.Literal('topdown'),
            Type.Literal('orbit'),
          ], {
            description: 'player=当前镜头（默认），topdown=临时俯视，orbit=绕目标点换角度',
          }),
        ),
        entityId: Type.Optional(Type.Integer({ description: '取景中心：建筑 id（topdown/orbit）' })),
        center: Type.Optional(
          Type.Object({
            latitude: Type.Optional(Type.Number()),
            longitude: Type.Optional(Type.Number()),
            altitude: Type.Optional(Type.Number()),
            x: Type.Optional(Type.Number()),
            y: Type.Optional(Type.Number()),
            z: Type.Optional(Type.Number()),
          }, {
            description: '取景中心：latitude+longitude；x/y/z 仅兼容旧调用（topdown/orbit）',
          }),
        ),
        cameraHeight: Type.Optional(Type.Number({ description: 'topdown 高度（米），默认 60，范围 10–150' })),
        distance: Type.Optional(Type.Number({ description: 'orbit 环绕半径（米），默认 80，范围 10–500' })),
        pitch: Type.Optional(Type.Number({ description: 'orbit 俯角（度），默认 35，范围 5–90；90=正俯视' })),
        yaw: Type.Optional(Type.Number({ description: 'topdown/orbit 方位角（度），默认 0' })),
        fov: Type.Optional(Type.Number({ description: 'topdown/orbit 视场角，默认 60，范围 20–90' })),
      }),
      timeoutMs: 15000,
    }),
    gameTool(host, {
      name: 'game_render_layout',
      label: '布局图',
      description:
        '把一片区域的建筑排布画成字符网格（上=+Z，左=-X）：物流站 S、生产设备 M、仓储 B、发电 P、' +
        '采矿 V、分拣器 >、传送带 =。用来判断“布局对不对/有没有断路/密度如何”，比截图更省 token。',
      promptGuidelines: [
        '玩家问布局、走线、是否堵死，或截图看不清时使用。',
        '字符只表达类别与占位；朝向、配方、输入输出要再用 game_get_entities / game_analyze_area。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        entityId: Type.Optional(Type.Integer({ description: '以该建筑为中心' })),
        radius: Type.Optional(Type.Number({ description: '半径（米），默认 40，范围 4–400' })),
        cellSize: Type.Optional(Type.Number({ description: '每格米数，默认按半径自动（约 2–8m）' })),
      }),
      timeoutMs: 10000,
    }),
    gameTool(host, {
      name: 'game_get_planets',
      label: '星球一览',
      description:
        '列出银河里所有行星：轨道/类型/半径、是否有工厂与建筑数、电力汇总，以及按宇宙探索等级披露的矿脉。' +
        '未观察的星球只给基础轨道信息（veinsHidden=true），不泄露矿脉；已建工厂的星球可继续用区域工具分析。',
      promptGuidelines: [
        '玩家问“哪个星球有 X 矿/哪颗星在产什么/银河整体情况”时使用。',
        'observed=false 或 veinsHidden=true 表示玩家还没探索到，不要编造矿脉；如实说明需要提升宇宙探索等级。',
        'hasFactory=true 的行星可继续用 game_analyze_area / game_get_entities / game_get_power 分析。',
      ],
      parameters: Type.Object({
        includeVeins: Type.Optional(Type.Boolean({ description: '是否返回已观察星球的矿脉，默认 true' })),
      }),
      timeoutMs: 20000,
    }),
    gameTool(host, {
      name: 'game_trace_line',
      label: '产线拓扑',
      description:
        '从一栋建筑出发，沿传送带与分拣器把相连设备串成图：节点（id/名称/类别/星系/行星/经纬度/深度）与有向边。' +
        '用于回答“这条线从哪到哪、哪里断了、上下游有哪些机器”。',
      promptGuidelines: [
        '玩家问某台机器的上下游、整条线是否连通、断在哪时使用。',
        '结果只含实体连接，不含配方流向与星际物流；跨星用 game_get_logistics。',
        'truncated=true 表示达到深度/节点上限，可调 depth / maxNodes 继续。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        entityId: Type.Integer({ description: '起点建筑 id（来自 game_get_entities / 截图）' }),
        depth: Type.Optional(Type.Integer({ minimum: 1, maximum: 60, description: '最大跳数，默认 20' })),
        maxNodes: Type.Optional(Type.Integer({ minimum: 2, maximum: 300, description: '最大节点数，默认 200' })),
      }),
      timeoutMs: 15000,
    }),
    gameTool(host, {
      name: 'space_region',
      label: '区域查询',
      description:
        '按区域聚合：成员列表、存量、输入/输出/生产/消耗速度、电力、上级、位置、静态信息、备忘录。' +
        '区域类型：恒星系 / 恒星 / 行星 / 电网 / 工厂 / 太空黑雾巢穴 / 地面黑雾基地 / 戴森球 / 世界区域 / 行星区域 / 蓝图。' +
        'op 与 dsp-agent 表的方法行同名，省略 target 时按玩家所在位置解析。',
      promptGuidelines: [
        '玩家问“某个区域里有什么/产出多少/耗多少电/在哪”时使用；不要自己拿经纬度去拼半径。',
        'op=storage 是存量，op=input/output 是物流进出，op=production/consumption 是生产消耗，两者不可相加。',
        'stat.windowNative=false 表示该对象没有分档统计，window 不生效，不要拿它当十分钟均值。',
        'op=navigation 不接受：导航是写操作，请改用 game_tether_add。',
      ],
      parameters: Type.Object({
        op: Type.Union([
          Type.Literal('list'),
          Type.Literal('storage'),
          Type.Literal('input'),
          Type.Literal('output'),
          Type.Literal('production'),
          Type.Literal('consumption'),
          Type.Literal('power'),
          Type.Literal('parent'),
          Type.Literal('location'),
          Type.Literal('information'),
          Type.Literal('note'),
        ], {
          description:
            'list=成员 storage=存量 input/output=物流进出 production/consumption=生产消耗 ' +
            'power=电力 parent=上级 location=位置 information=静态信息 note=备忘录',
        }),
        type: Type.Optional(Type.Union([
          Type.Literal('auto'),
          Type.Literal('star_system'),
          Type.Literal('star'),
          Type.Literal('planet'),
          Type.Literal('power_grid'),
          Type.Literal('factory'),
          Type.Literal('df_hive'),
          Type.Literal('df_base'),
          Type.Literal('dyson_sphere'),
          Type.Literal('world_box'),
          Type.Literal('planet_patch'),
          Type.Literal('blueprint'),
        ], {
          description: '区域类型；auto 时按玩家所在位置推断（在行星上=planet，否则=star_system）',
        })),
        target: Type.Optional(Type.Union([Type.String(), Type.Object({})], {
          description:
            '对象实例：字符串给 id（planetId / starId / networkId:seedEntityId / 蓝图路径）；' +
            'world_box 与 planet_patch 可传对象带 min/max 或 latMin/latMax/lonStart/lonSpan',
        })),
        window: Type.Optional(Type.Union([
          Type.Literal('1m'), Type.Literal('10m'), Type.Literal('1h'),
          Type.Literal('10h'), Type.Literal('100h'), Type.Literal('all'),
        ], { description: '统计时间窗，默认 10m' })),
        unit: Type.Optional(Type.Union([
          Type.Literal('perMin'), Type.Literal('perHour'), Type.Literal('total'),
        ], { description: '输出单位，默认 perMin' })),
        basis: Type.Optional(Type.Union([
          Type.Literal('actual'), Type.Literal('installed'),
        ], { description: 'actual=实际统计（默认），installed=按配方与建筑数算的理论值' })),
        itemFilter: Type.Optional(Type.String({ description: 'list 时按成员类别过滤；统计时按物品名或 id 过滤' })),
        depth: Type.Optional(Type.Integer({ minimum: 1, maximum: 4, description: 'parent 的层数，默认 1' })),
        radius: Type.Optional(Type.Number({ description: '几何区域半径（米），默认 200' })),
        frame: Type.Optional(Type.Union([
          Type.Literal('auto'), Type.Literal('planet'), Type.Literal('world'),
        ], { description: 'location 的坐标系，默认 auto' })),
        precision: Type.Optional(Type.Integer({ minimum: 0, maximum: 6, description: '经纬度小数位，默认 3' })),
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 15000,
    }),
    gameTool(host, {
      name: 'space_point',
      label: '定点查询',
      description:
        '按定点聚合：下辖动点、存量、输入/输出/生产/消耗、电力、归属、行星坐标、备忘录。' +
        '定点类型：建筑 / 矿脉 / 植被 / 太空黑雾建筑 / 地面黑雾建筑 / 世界坐标 / 行星坐标。',
      promptGuidelines: [
        '玩家指着某栋建筑/某个矿脉问“它属于谁/在哪/多少电/进出多少”时使用。',
        '省略 target 时先看玩家瞄准的对象，再退到最近的同类；找不到会明确报错，不会静默换目标。',
        '矿脉的 output 是按开采机数量算出的理论值（basis=installed），矿脉自身没有实际统计。',
        'op=navigation 不接受：导航是写操作，请改用 game_tether_add。',
      ],
      parameters: Type.Object({
        op: Type.Union([
          Type.Literal('list'),
          Type.Literal('storage'),
          Type.Literal('input'),
          Type.Literal('output'),
          Type.Literal('production'),
          Type.Literal('consumption'),
          Type.Literal('power'),
          Type.Literal('parent'),
          Type.Literal('location'),
          Type.Literal('note'),
        ], {
          description:
            'list=下辖动点 storage=存量 input/output=物流进出 production/consumption=生产消耗 ' +
            'power=电力 parent=归属 location=坐标 note=全息信标备忘录',
        }),
        type: Type.Optional(Type.Union([
          Type.Literal('auto'),
          Type.Literal('building'),
          Type.Literal('vein'),
          Type.Literal('vegetation'),
          Type.Literal('df_space_building'),
          Type.Literal('df_ground_building'),
          Type.Literal('world_position'),
          Type.Literal('planet_position'),
        ], { description: '定点类型；auto 时默认 building' })),
        target: Type.Optional(Type.String({
          description: 'id：entityId / veinId / vegeId / enemyId；省略时按玩家瞄准或最近解析',
        })),
        window: Type.Optional(Type.Union([
          Type.Literal('1m'), Type.Literal('10m'), Type.Literal('1h'),
          Type.Literal('10h'), Type.Literal('100h'), Type.Literal('all'),
        ], { description: '统计时间窗，默认 10m' })),
        unit: Type.Optional(Type.Union([
          Type.Literal('perMin'), Type.Literal('perHour'), Type.Literal('total'),
        ], { description: '输出单位，默认 perMin' })),
        basis: Type.Optional(Type.Union([
          Type.Literal('actual'), Type.Literal('installed'),
        ], { description: 'actual=实际统计（默认），installed=按配方算的理论值' })),
        itemFilter: Type.Optional(Type.String({ description: '统计时按物品名或 id 过滤' })),
        frame: Type.Optional(Type.Union([
          Type.Literal('auto'), Type.Literal('planet'), Type.Literal('world'),
        ], { description: 'location 的坐标系，默认 auto' })),
        precision: Type.Optional(Type.Integer({ minimum: 0, maximum: 6, description: '经纬度小数位，默认 3' })),
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 12000,
    }),
    gameTool(host, {
      name: 'space_motion',
      label: '动点查询',
      description:
        '按动点读位置与状态：玩家 / 建设无人机 / 配送运输机 / 物流运输机 / 星际物流运输船 / ' +
        '小型运载火箭 / 战斗无人机 / 战舰 / 太空黑雾单位 / 地面黑雾单位 / 垃圾块。' +
        '位置带 gameTick，跨回合比较前要确认是同一 tick 的快照。',
      promptGuidelines: [
        '玩家问“那架无人机/那艘船在哪、在干什么、还剩多少货”时使用。',
        '动点会移动：本工具只给当前快照，没有追踪；需要新位置就再调一次，不要拿旧值推算。',
        'op=storage 只对玩家与运输类动点有效；op=parent 对垃圾块返回无归属。',
        'op=navigation 不接受：动点无法用 tether 追踪。',
      ],
      parameters: Type.Object({
        op: Type.Union([
          Type.Literal('list'),
          Type.Literal('storage'),
          Type.Literal('parent'),
          Type.Literal('location'),
          Type.Literal('information'),
        ], {
          description: 'list=玩家下辖动点 storage=携带物品 parent=归属 location=位置 information=状态',
        }),
        type: Type.Optional(Type.Union([
          Type.Literal('auto'),
          Type.Literal('player'),
          Type.Literal('construction_drone'),
          Type.Literal('delivery_courier'),
          Type.Literal('station_drone'),
          Type.Literal('station_ship'),
          Type.Literal('dyson_rocket'),
          Type.Literal('combat_drone'),
          Type.Literal('combat_fleet'),
          Type.Literal('df_space_unit'),
          Type.Literal('df_ground_unit'),
          Type.Literal('trash'),
        ], { description: '动点类型；auto 时默认 player' })),
        target: Type.Optional(Type.String({
          description: 'id：craftId / enemyId / trashId / rocketId / stationGid:shipIndex；省略时取最近的一个',
        })),
        precision: Type.Optional(Type.Integer({ minimum: 0, maximum: 6, description: '经纬度小数位，默认 3' })),
        offset: offsetArg,
        limit: limitArg,
      }),
      timeoutMs: 12000,
    }),
    gameTool(host, {
      name: 'game_get_logistics',
      label: '星际物流',
      description:
        '汇总所有已加载工厂的物流塔：哪些星球/站点在供给或需求某物品、星际塔与轨道采集器、' +
        '运输船/无人机与翘曲器数量。这是跨行星产线分析的入口。',
      promptGuidelines: [
        '玩家问“某个原料从哪来/哪颗星在供应/为什么跨星运不过来”时使用。',
        'supply/demand 来自物流塔槽位的远程供应/需求设置；具体航线与在途量用 game_get_entity 看站点详情。',
        '先在这里定位物品与星球，再用 game_analyze_area / game_get_planet 下钻。',
      ],
      parameters: Type.Object({
        itemFilter: Type.Optional(Type.String({ description: '物品名或 id，例如“钛块”' })),
        planetId: Type.Optional(Type.Integer({ description: '只看某颗行星' })),
        offset: offsetArg,
        limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 40, description: '站点条数，默认 20' })),
      }),
      timeoutMs: 20000,
    }),
    gameTool(host, {
      name: 'game_copy_blueprint',
      label: '存为蓝图',
      description:
        '把指定的一批建筑（entityIds）存成游戏蓝图文件，复用游戏自己的复制逻辑，产物与游戏内复制同源。' +
        '保存到游戏蓝图目录（默认 IcarusII/，可传 dir），打开蓝图界面即可看到；只写蓝图文件，不改游戏状态。',
      promptGuidelines: [
        '玩家说“把这片产线存成蓝图”时使用；id 来自 game_analyze_area / game_get_entities。',
        '一次最多 500 个；工具只保存文件，不建造、不粘贴；权限档位会决定是否弹确认。',
        '保存后告诉玩家文件名与位置；重名会自动加 (2)/(3)。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        entityIds: Type.Array(Type.Integer({ minimum: 1 }), { description: '要包含的建筑 id' }),
        name: Type.Optional(Type.String({ description: '蓝图文件名（不含 .txt），默认 IcarusII-时间戳' })),
        dir: Type.Optional(
          Type.String({ description: '保存目录（相对蓝图根，默认 IcarusII）；不存在会自动创建' }),
        ),
      }),
      timeoutMs: 20000,
      // 只写蓝图文件、不改游戏状态；risk 1：只读权限下仍需确认。
      risk: 1,
    }),
    gameTool(host, {
      name: 'game_analyze_area',
      label: '产线分析',
      description:
        '按空间范围聚合产线：各类建筑数量、各配方的机器数与装机产能、耗电、传送带/分拣器/物流塔数量、' +
        '涉及电网的供需。用于回答“这片区域是什么产线、规模多大、吃多少电”。' +
        '只给装机口径；实际产出请再用 game_get_production。',
      promptGuidelines: [
        '玩家问“这片产线是干什么的/多大/耗多少电”时使用。',
        'game_get_production 返回 statsEmpty=true 或 items 为空时，用它看装机产能（可 all=true 扫整颗星球）。',
        '结果里 byRecipe 是“机器数 × 配方速度”，不要把它说成实际产出。',
        '结果是装机产能，不是实际产出；回答实际产量要结合 game_get_production。',
        '范围默认以玩家为中心 60m；可用 entityId 指定中心，或用 all 分析整星球。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        entityId: Type.Optional(Type.Integer({ description: '以该建筑为圆心' })),
        radius: Type.Optional(Type.Number({ description: '半径（米），默认 60，范围 5–500' })),
        center: Type.Optional(
          Type.Object({
            latitude: Type.Optional(Type.Number()),
            longitude: Type.Optional(Type.Number()),
            altitude: Type.Optional(Type.Number()),
            x: Type.Optional(Type.Number()),
            y: Type.Optional(Type.Number()),
            z: Type.Optional(Type.Number()),
          }, {
            description: '中心：latitude+longitude；x/y/z 仅兼容旧调用',
          }),
        ),
        all: Type.Optional(Type.Boolean({ description: '分析整颗星球，忽略 radius/center' })),
        includeEntities: Type.Optional(Type.Boolean({ description: '是否列出范围内建筑，默认 false' })),
        limit: Type.Optional(Type.Integer({ description: '列出的建筑上限，默认 40，最大 100' })),
      }),
      timeoutMs: 15000,
    }),
    gameTool(host, {
      name: 'game_todo_templates',
      label: '目标模板',
      description:
        '列出游戏原生 GoalProto 目标模板：id、名称、判断器、等级、父分组和默认参数。' +
        '生成游戏目标前先调用它，选择真实存在的判断器/模板。',
      promptGuidelines: [
        '创建游戏目标前先查；不要凭记忆编造 GoalProto 模板 id。',
        'params 是原生 DeterminatorParams 的默认值；game_todo_add 可覆盖它们。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 5000,
    }),
    gameTool(host, {
      name: 'game_todo_list',
      label: '当前游戏目标',
      description: '读取 AI 创建的原生游戏目标（含轮内待办）及当前值/目标值/阶段，以及目标面板当前是否真的没有显示。',
      promptGuidelines: [
        '玩家询问当前目标进度或准备删除目标时使用。',
        '目标在“自由探索”下也会显示（只列 AI 目标）；如果结果里 goalPanelOff=true，说明目标面板确实被关闭了，回答进度时提醒玩家打开目标面板设置改成“关键节点”或“事无巨细”。',
        'todo=true 的行是轮内待办，用 game_todo_write 更新；不要用 game_todo_remove 删它们。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 5000,
    }),
    gameTool(host, {
      name: 'mods_api_list',
      label: 'mod 可调用方法',
      description:
        '只读列出某个插件程序集里可被 manifest 调用的公开静态方法：类型、方法、参数、返回值。' +
        '同时返回 uiTexts（该插件窗口里的文字）与 configHints（配置项说明），' +
        '用于给没有 manifest 的 mod 起草适配器；只读元数据，不执行任何代码。',
      promptGuidelines: [
        '玩家要求为某个 mod 生成适配、且它没有 manifest 时使用。',
        '先读 uiTexts 与 configHints 理解功能，方法名只是线索；不要凭方法名猜语义。',
        'uiTexts 为空时，让玩家先打开该 mod 的窗口再调一次。',
        '只把这里列出的方法写进 tools，不要猜方法名或参数。',
        'parameters 必须用标准 JSON Schema 类型：object/array/string/number/integer/boolean；不要写 int/bool/float 这类别名。',
        '配置能解决的问题优先用配置；适配器只用于“要执行一次动作”的场景。',
      ],
      parameters: Type.Object({
        guid: Type.String({ description: '插件 GUID，来自 mods_list' }),
      }),
      timeoutMs: 5000,
    }),
    gameTool(host, {
      name: 'mods_adapter_create',
      label: '创建 mod 适配器',
      description:
        '把某个第三方 mod 的公开静态方法绑定成工具，写入 BepInEx/config/icarus-ii/bridge/<guid>.json 并热载。' +
        '写操作：执行前玩家必须确认；创建后下一轮生效。',
      promptGuidelines: [
        '先调 mods_api_list 拿真实的方法签名；用 mods_list 的 adapter 字段判断是否已有适配器。',
        '一次只为一个 mod 创建，工具数量控制在 1-3 个，优先只读或可逆的方法。',
        '参数类型只支持 int/long/float/double/bool/string/enum；不要绑定对象或委托。',
        'parameters 的 type 必须写 integer/number/boolean/string/array/object，写 int/bool 会导致上游拒绝整个工具表。',
        '创建前把“调用哪个方法、参数是什么”讲清楚，等玩家确认。',
      ],
      parameters: Type.Object({
        guid: Type.String({ description: '插件 GUID，来自 mods_list' }),
        tools: Type.Array(
          Type.Object(
            {
              name: Type.String({ description: '短名，只允许小写字母/数字/下划线' }),
              title: Type.Optional(Type.String({ description: '显示名' })),
              description: Type.Optional(Type.String({ description: '模型看到的说明' })),
              timeoutMs: Type.Optional(Type.Integer({ minimum: 500, maximum: 30000 })),
              parameters: Type.Optional(Type.Object({}, { additionalProperties: true })),
              invoke: Type.Object(
                {
                  type: Type.String({ description: '插件程序集里的类型全名' }),
                  method: Type.String({ description: '公开静态方法名' }),
                  args: Type.Optional(Type.Array(Type.Any(), { description: '参数映射' })),
                },
                { additionalProperties: true },
              ),
              undo: Type.Optional(
                Type.Object(
                  {
                    type: Type.String(),
                    method: Type.String(),
                    args: Type.Optional(Type.Array(Type.Any())),
                  },
                  { additionalProperties: true },
                ),
              ),
            },
            { additionalProperties: true },
          ),
          { description: '要绑定的工具列表，最多 12 个', minItems: 1, maxItems: 12 },
        ),
        replace: Type.Optional(Type.Boolean({ description: '覆盖已有适配器，默认 false' })),
      }),
      timeoutMs: 8000,
      risk: 2,
    }),
    gameTool(host, {
      name: 'mods_adapter_delete',
      label: '删除 mod 适配器',
      description:
        '删除 config/icarus-ii/bridge 下的适配器文件并热载；随包与内置适配器不受影响。' +
        '写操作：执行前玩家必须确认，撤销可恢复文件。',
      promptGuidelines: [
        '只在玩家要求撤销/清理某个 AI 生成的适配器时使用。',
        '删除后该 mod 的动作工具立即消失，但配置工具仍然可用。',
      ],
      parameters: Type.Object({
        guid: Type.String({ description: '插件 GUID，来自 mods_list' }),
      }),
      timeoutMs: 5000,
      risk: 2,
    }),
    gameTool(host, {
      name: 'ui_snapshot',
      label: '界面控件',
      description:
        '列出当前可见的 uGUI 可交互控件：层级路径、类型、显示文字、是否可用、Toggle/InputField 的值。' +
        '用 filter 按文字或路径过滤；只覆盖 uGUI，自绘/OnGUI 界面看不到。',
      promptGuidelines: [
        '需要操作没有专用工具的 mod 面板时先快照，按 label 找控件；不要凭像素猜位置。',
        '路径带 #序号，必须原样传给 ui_click。',
        '快照只读；点击后建议再快照一次确认界面变化。',
      ],
      parameters: Type.Object({
        filter: Type.Optional(Type.String({ description: '按显示文字或路径过滤（不区分大小写）' })),
        includeHidden: Type.Optional(Type.Boolean({ description: '包含未激活节点，默认 false' })),
      }),
      timeoutMs: 5000,
    }),
    gameTool(host, {
      name: 'ui_click',
      label: '点击界面控件',
      description:
        '按 ui_snapshot 给出的路径派发一次合成点击（PointerClick）。写操作：执行前玩家必须确认；' +
        '语义未知、不可撤销，点击后请用 ui_snapshot 复核。',
      promptGuidelines: [
        '只点击 ui_snapshot 里出现、且 interactable=true 的控件。',
        '点完必须再快照确认；没变化就如实说“可能没生效”，不要假装成功。',
      ],
      parameters: Type.Object({
        path: Type.String({ description: 'ui_snapshot 返回的 path，原样传入' }),
      }),
      timeoutMs: 5000,
      risk: 2,
    }),
    gameTool(host, {
      name: 'mods_list',
      label: '已装 mod 列表',
      description:
        '列出除本 mod 以外、当前已启用的 BepInEx 插件：GUID、id、名称、版本、程序集、配置项数量、是否加载失败、适配器状态。' +
        '需要操作玩家安装的其他 mod 时先调用它，不要凭玩家口述猜 mod 是否存在。',
      promptGuidelines: [
        '玩家提到“我装的某个 mod”“帮我开关某个 mod 的功能”时先调用。',
        'loadFailed=true 的插件只报告，不要尝试读写它的配置。',
        '工具不存在说明玩家还没在设置 → 工具 → mod 里启用任何 mod。',
        '想看某个 mod 有没有可调用的动作，看它返回的 adapter 字段：none 表示只能读配置。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 5000,
    }),
  ];

  tools.push(...createWriteTools(host));
  const filtered = tools.filter((tool) => {
    const capability = TOOL_CAPABILITY[tool.name];
    return capability !== undefined && host.has(capability);
  });
  if (host.has(CAPABILITY_MOD_BRIDGE)) {
    for (const extension of host.extensionList ?? []) {
      if (extension.configCount > 0) filtered.push(createExtensionConfigTool(host, extension));
      for (const tool of extension.tools ?? []) filtered.push(createExtensionInvokeTool(host, tool));
    }
  }
  return filtered;
}

/** manifest 声明的第三方工具：名字、schema、risk 全部来自插件；插件不在就没有它。 */
function createExtensionInvokeTool(host: GameToolHost, tool: ExtensionTool): ToolDefinition {
  return {
    name: tool.name,
    label: tool.label,
    description: tool.description || `${tool.label}（第三方 mod 工具）`,
    promptGuidelines: [
      `这是 ${tool.label} 提供的第三方 mod 工具；只在对应插件加载时存在。`,
      tool.risk > 0 ? '这是写操作，执行前会请求确认；可撤销性由该 mod 的适配器声明。' : '只读工具。',
    ],
    parameters: tool.parameters as never,
    executionMode: 'sequential',
    async execute(_toolCallId, params, signal) {
      const result = await host.call(tool.name, (params ?? {}) as JsonObject, {
        timeoutMs: tool.timeoutMs,
        signal,
        risk: tool.risk,
      });
      return { content: buildToolContent(result), details: result };
    },
  };
}

/** 每个第三方插件一个配置工具；插件不在 hello.extensions 里时这个工具就不存在。 */
export function extensionToolName(id: string): string {
  return `mod_${id}_config`;
}

function createExtensionConfigTool(host: GameToolHost, extension: ExtensionSummary): ToolDefinition {
  const name = extensionToolName(extension.id);
  return {
    name,
    label: `${extension.name} 配置`,
    description:
      `读写 ${extension.name}（GUID ${extension.guid}，版本 ${extension.version}）的配置：` +
      'action=list 列出、get 读取单个、set 写入。这个工具只在该 mod 已加载时存在；' +
      'set 是写操作，需要确认，可用 game_undo_last_write 撤销。',
    promptGuidelines: [
      `只操作 ${extension.name}；不要拿别的 mod 的 GUID 调它。`,
      'set 前先用 get 或 list 确认 key 与当前值；部分 mod 只在启动时读配置，改完可能需要重启。',
      '配置项的 description 是 mod 作者写的说明，回答“这个开关是干什么的”时优先引用它。',
    ],
    parameters: Type.Object({
      action: Type.Union([Type.Literal('list'), Type.Literal('get'), Type.Literal('set')], {
        description: 'list = 分页列出；get = 读一个；set = 写一个',
      }),
      section: Type.Optional(Type.String({ description: '配置分组' })),
      key: Type.Optional(Type.String({ description: '配置项 key；get/set 必填' })),
      value: Type.Optional(
        Type.Union([Type.String(), Type.Number(), Type.Boolean()], {
          description: 'set 的新值；按配置项类型解析',
        }),
      ),
      offset: Type.Optional(Type.Integer({ minimum: 0, description: 'list 的起始偏移' })),
      limit: Type.Optional(Type.Integer({ minimum: 1, maximum: 200, description: 'list 每页条数' })),
    }),
    executionMode: 'sequential',
    async execute(_toolCallId, params, signal) {
      const args = (params ?? {}) as Record<string, unknown>;
      const action = typeof args['action'] === 'string' ? args['action'] : '';
      const section = typeof args['section'] === 'string' ? args['section'] : undefined;
      const key = typeof args['key'] === 'string' ? args['key'] : undefined;

      const invalid = (
        message: string,
      ): { content: ReturnType<typeof buildToolContent>; details: ToolResultPayload } => {
        const result = toolError('invalid_args', message);
        return { content: buildToolContent(result), details: result };
      };

      if (action === 'list') {
        const call: JsonObject = { guid: extension.guid };
        if (section !== undefined) call['section'] = section;
        if (typeof args['offset'] === 'number') call['offset'] = args['offset'];
        if (typeof args['limit'] === 'number') call['limit'] = args['limit'];
        const result = await host.call('mods_config_list', call, { timeoutMs: 5000, signal, risk: 0 });
        return { content: buildToolContent(result), details: result };
      }
      if (action === 'get') {
        if (!key) return invalid('get 需要 key');
        const call: JsonObject = { guid: extension.guid, key };
        if (section !== undefined) call['section'] = section;
        const result = await host.call('mods_config_get', call, { timeoutMs: 5000, signal, risk: 0 });
        return { content: buildToolContent(result), details: result };
      }
      if (action === 'set') {
        if (!key || section === undefined || args['value'] === undefined) {
          return invalid('set 需要 section、key 与 value');
        }
        const call: JsonObject = {
          guid: extension.guid,
          section,
          key,
          value: args['value'] as string | number | boolean,
        };
        const result = await host.call('mods_config_set', call, { timeoutMs: 8000, signal, risk: 1 });
        return { content: buildToolContent(result), details: result };
      }
      return invalid('action 必须是 list / get / set');
    },
  };
}

registerGameToolsFactory(createGameTools);

/** `ready.payload.tools` 用：按能力返回本次连接实际可用的工具名。 */
export function toolNamesForCapabilities(
  capabilities: readonly string[],
  extensions: readonly ExtensionSummary[] = [],
): string[] {
  // kb_* 是 sidecar 本地工具，不依赖 mod 能力；始终列出，未建立时返回 kb_unavailable。
  const game = Object.entries(TOOL_CAPABILITY)
    .filter(([, capability]) => capabilities.includes(capability))
    .map(([name]) => name);
  // 交互问答依赖 mod 的 choose UI；老 mod 不会声明该能力。
  const ask = capabilities.includes(CAPABILITY_UI_ASK) ? [UI_ASK_TOOL_NAME] : [];
  // 每个已加载的第三方插件一个配置工具；插件不在 extensions 里时名字也不出现。
  const bridge = capabilities.includes(CAPABILITY_MOD_BRIDGE)
    ? extensions.flatMap((extension) => [
        ...(extension.configCount > 0 ? [extensionToolName(extension.id)] : []),
        ...(extension.tools ?? []).map((tool) => tool.name),
      ])
    : [];
  // 脚本工具是 sidecar 本地能力，始终注册（没有 game 工具时执行会明确报错）。
  return [
    ...ask,
    ...game,
    ...bridge,
    ...KB_TOOL_NAMES,
    ...WIKI_TOOL_NAMES,
    ...BLUEPRINT_TOOL_NAMES,
    SCRIPT_TOOL_NAME,
  ];
}

// ---------------------------------------------------------------- A.14.1 目录元数据

/**
 * 本地工具里的写操作（risk 1）。它们不在 WRITE_TOOL_NAMES 里：
 * 那几个名字只登记「走 mod 游戏侧确认」的写工具，本地文件/脚本写用各自的确认。
 */
const LOCAL_WRITE_TOOLS: readonly string[] = [
  'blueprint_fs',
  'blueprint_save',
  'blueprint_generate',
  SCRIPT_TOOL_NAME,
];

/** 工具在设定页里显示的风险标记：0 = 只读，1 = 可逆写，2 = 改变存档。 */
export function toolRiskOf(name: string): 0 | 1 | 2 {
  if (isWriteToolName(name)) return writeToolRisk(name);
  if (LOCAL_WRITE_TOOLS.includes(name)) return 1;
  return 0;
}

/** 工具分组；只用于设定页筛选，不影响执行。 */
export function toolGroupOf(name: string): ToolGroup {
  if (name.startsWith('mod_') || name.startsWith('mods_')) return 'thirdparty';
  if ((KB_TOOL_NAMES as readonly string[]).includes(name)) return 'kb';
  if ((WIKI_TOOL_NAMES as readonly string[]).includes(name)) return 'kb';
  if ((BLUEPRINT_TOOL_NAMES as readonly string[]).includes(name)) return 'blueprint';
  if (name === UI_ASK_TOOL_NAME || (UI_ASK_TOOL_NAME_ALIASES as readonly string[]).includes(name)) return 'ask';
  if (name.startsWith('game_')) return toolRiskOf(name) > 0 ? 'game.write' : 'game.read';
  // 脚本、界面点击与其余杂项合并为「其他」。
  return 'other';
}

/** 第三方工具名（`mod_<id>_<name>`）→ 来源；其他工具都算内置。 */
export function toolSourceOf(
  name: string,
  extensions: readonly ExtensionSummary[] = [],
): { source: string; sourceName: string } {
  if (name.startsWith('mod_')) {
    for (const extension of extensions) {
      if (name.startsWith(`mod_${extension.id}_`)) {
        return { source: extension.id, sourceName: extension.name || extension.id };
      }
    }
    return { source: 'thirdparty', sourceName: '第三方插件' };
  }
  return { source: 'builtin', sourceName: '内置' };
}
