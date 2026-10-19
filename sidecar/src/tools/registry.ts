/**
 * M2 只读游戏工具定义（协议附录 B）。
 *
 * 这些工具只声明 schema 与调用方式；真正的采集在 mod 侧。
 * 工具名使用下划线：OpenAI 兼容端点会把非 [a-zA-Z0-9_-] 字符替换成 `_`，
 * 用点号会导致模型看到的工具名与回传的 tool_call.name 不一致。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import type { JsonObject, ToolResultPayload } from '../protocol.js';
import { GameToolHost, registerGameToolsFactory, SCRIPT_TOOL_NAME } from './host.js';
import { WIKI_TOOL_NAMES } from '../wiki/tools.js';
import { BLUEPRINT_TOOL_NAMES } from '../blueprint/tools.js';
import { KB_TOOL_NAMES } from '../kb/tools.js';

export const CAPABILITY_GAME_READ = 'game.read.v1';
export const CAPABILITY_GAME_ENTITY = 'game.entity.v1';
export const CAPABILITY_GAME_PROGRESS = 'game.progress.v1';
export const CAPABILITY_GAME_VIEW = 'game.view.v1';
export const CAPABILITY_GAME_ANALYZE = 'game.analyze.v1';

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
  game_generate_blueprint: CAPABILITY_GAME_ENTITY,
  game_trace_line: CAPABILITY_GAME_ENTITY,
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
    promptSnippet: string;
    promptGuidelines?: string[];
    parameters: ReturnType<typeof Type.Object>;
    timeoutMs: number;
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
        '读取当前存档概览：存档名、游戏时刻、当前行星/星系、玩家坐标、是否暂停。' +
        '玩家问“我在哪”“现在什么情况”时先调用它；不要凭印象回答。',
      promptSnippet: '读取当前存档、星球、时间与玩家位置的概览',
      promptGuidelines: [
        'Use game_get_overview 当玩家询问当前存档、所在星球、游戏时间或“现在什么情况”时。',
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
      promptSnippet: '读取玩家机甲能量、护盾与背包物品',
      promptGuidelines: [
        'Use game_get_player 当玩家询问自己的能量、护盾或背包物品时。',
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
        '远程行星未加载工厂时只返回静态矿脉，并明确标注 factoryLoaded=false。',
      promptSnippet: '读取行星矿脉汇总与建筑计数',
      promptGuidelines: [
        'Use game_get_planet 当玩家询问某个星球有什么矿、建了多少建筑时。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
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
      promptSnippet: '读取电网需求、供给、容量、储能与满足率',
      promptGuidelines: [
        'Use game_get_power 当玩家询问电力是否足够、为什么停电或电网负载时。',
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
      promptSnippet: '读取当前研究、队列、进度与已解锁数量',
      promptGuidelines: [
        'Use game_get_research 当玩家询问研究进度、科技队列或已解锁科技时。',
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
      promptSnippet: '读取产线净产出、消耗、库存与缺料分钟数',
      promptGuidelines: [
        'Use game_get_production 当玩家问“我缺什么”、某条产线为什么停、库存还能撑多久时。',
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
        '按 entityId 读取单个建筑的实际情况，尽量对齐游戏面板：原型、配方、输入/输出槽（数量/需求/增产）、' +
        '功率与供电满足率、工作状态（缺料/没电/运行/待机）、分拣器目标、传送带线路容量/节点/货物数与区间、' +
        '仓储容量、物流塔槽位（当前/在途/上限/供需模式）、无人机/运输船/翘曲器与充电状态。' +
        'id 来自 game_get_entities 或截图标注；id 会被回收，返回里的 stale=true 表示该 id 已被别的建筑占用。',
      promptSnippet: '按 id 读取单个建筑的配方、功率与连接配置',
      promptGuidelines: [
        'Use game_get_entity 当玩家问某一栋建筑为什么不动、配方是什么、插了什么、有没有电时。',
        '如果返回 stale=true，不要使用这条数据：id 已被回收，重新用 game_get_entities 定位。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        entityId: Type.Integer({ description: '建筑 id（PlanetFactory.entityPool 的下标）' }),
        expectProtoId: Type.Optional(
          Type.Integer({ description: '期望的原型 id；不匹配时返回 stale=true' }),
        ),
      }),
      timeoutMs: 6000,
    }),
    gameTool(host, {
      name: 'game_get_entities',
      label: '建筑列表',
      description:
        '分页列出指定行星（省略则为当前行星）的建筑：id、原型、配方、坐标、朝向。' +
        '可按原型 id/名称、配方 id 过滤；配合 game_get_entity 查看单体细节。' +
        '大星球请用 offset/limit 分页，不要一次拉全量。',
      promptSnippet: '分页列出建筑，可按原型或配方过滤',
      promptGuidelines: [
        'Use game_get_entities 当需要“这片区域有哪些建筑”“某个配方有多少台机器”时。',
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
      promptSnippet: '读取玩家阶段、已解锁科技、里程碑与研究队列',
      promptGuidelines: [
        'Use game_get_progress 当玩家询问当前进度、下一步该做什么、阶段目标时。',
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
        '玩家问“这里摆得对不对”“你看一眼”时调用。' +
        '截图只反映当前镜头；需要看别处时先用 game_focus_camera（尚未实现）或让玩家转视角。',
      promptSnippet: '截取当前画面并返回图片与可见实体坐标',
      promptGuidelines: [
        'Use game_capture_view 当玩家让你看画面、问布局/摆放/接线是否正确时。',
        '拿到图片后先描述你看到的，再结合 game_get_entity / game_get_entities 的数值下结论；不要只凭像素猜配方。',
      ],
      parameters: Type.Object({
        width: Type.Optional(Type.Integer({ description: '像素宽，默认 1280，上限 1600' })),
        height: Type.Optional(Type.Integer({ description: '像素高，默认 720，上限 900' })),
        includeEntities: Type.Optional(
          Type.Boolean({ description: '是否附带可见建筑的屏幕坐标，默认 true' }),
        ),
      }),
      timeoutMs: 15000,
    }),
    gameTool(host, {
      name: 'game_render_layout',
      label: '布局图',
      description:
        '把一片区域的建筑排布画成字符网格（上=+Z，左=-X）：物流站 S、生产设备 M、仓储 B、发电 P、' +
        '采矿 V、分拣器 >、传送带 =。用来判断“布局对不对/有没有断路/密度如何”，比截图更省 token。',
      promptSnippet: '输出区域建筑的字符布局图',
      promptGuidelines: [
        'Use game_render_layout 当玩家问布局、走线、是否堵死，或截图看不清时。',
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
      promptSnippet: '跨行星总览（受宇宙探索等级限制）',
      promptGuidelines: [
        'Use game_get_planets 当玩家问“哪个星球有 X 矿/哪颗星在产什么/银河整体情况”时。',
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
        '从一栋建筑出发，沿传送带与分拣器把相连设备串成图：节点（id/名称/类别/坐标/深度）与有向边。' +
        '用于回答“这条线从哪到哪、哪里断了、上下游有哪些机器”。',
      promptSnippet: '沿传送带/分拣器追踪产线拓扑',
      promptGuidelines: [
        'Use game_trace_line 当玩家问某台机器的上下游、整条线是否连通、断在哪时。',
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
      name: 'game_get_logistics',
      label: '星际物流',
      description:
        '汇总所有已加载工厂的物流塔：哪些星球/站点在供给或需求某物品、星际塔与轨道采集器、' +
        '运输船/无人机与翘曲器数量。这是跨行星产线分析的入口。',
      promptSnippet: '全银河物流塔的供给/需求与站点',
      promptGuidelines: [
        'Use game_get_logistics 当玩家问“某个原料从哪来/哪颗星在供应/为什么跨星运不过来”时。',
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
      name: 'game_generate_blueprint',
      label: '生成蓝图',
      description:
        '按模版从零生成一张只含生产设备的蓝图：给定配方与行列/间距（或显式 buildings 坐标），' +
        '自动选择同配方类型的基础设备并保存到游戏蓝图目录。传送带/分拣器需粘贴后自行连接。' +
        '产物是标准蓝图文本，与游戏内复制格式同源。',
      promptSnippet: '按配方与行列生成机器阵列蓝图',
      promptGuidelines: [
        'Use game_generate_blueprint 当玩家要“新建一张 N×M 的某配方产线蓝图”时。',
        '机器间默认 4 格间距（约 4m）；需要自定义坐标时传 buildings:[{protoId,recipeId,x,y,yaw}]。',
        '生成的是裸机器阵列，不含带子；回答里要说明这一点，并给出保存路径。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        recipeId: Type.Optional(Type.Integer({ description: '配方 id；buildings 模式可省略' })),
        protoId: Type.Optional(Type.Integer({ description: '设备原型 id；省略时按配方类型自动选基础档' })),
        rows: Type.Optional(Type.Integer({ minimum: 1, maximum: 40, description: '行数，默认 1' })),
        cols: Type.Optional(Type.Integer({ minimum: 1, maximum: 60, description: '列数，默认 1' })),
        spacingX: Type.Optional(Type.Number({ description: '列间距（格），默认 4' })),
        spacingY: Type.Optional(Type.Number({ description: '行间距（格），默认 4' })),
        originX: Type.Optional(Type.Number({ description: '起始 X（格），默认 0' })),
        originY: Type.Optional(Type.Number({ description: '起始 Y（格），默认 0' })),
        yaw: Type.Optional(Type.Number({ description: '朝向角度，默认 0' })),
        buildings: Type.Optional(
          Type.Array(
            Type.Object({
              protoId: Type.Integer(),
              recipeId: Type.Integer(),
              x: Type.Number(),
              y: Type.Number(),
              yaw: Type.Optional(Type.Number()),
            }),
            { description: '显式建筑列表，优先于行列模版；最多 800 台' },
          ),
        ),
        name: Type.Optional(Type.String({ description: '蓝图文件名，默认“IcarusII 产线”' })),
      }),
      timeoutMs: 20000,
    }),
    gameTool(host, {
      name: 'game_copy_blueprint',
      label: '存为蓝图',
      description:
        '把指定的一批建筑（entityIds）存成游戏蓝图文件，复用游戏自己的复制逻辑，产物与游戏内复制同源。' +
        '保存到游戏蓝图目录，打开蓝图界面即可看到；只写蓝图文件，不改游戏状态。',
      promptSnippet: '把一批建筑存成蓝图文件',
      promptGuidelines: [
        'Use game_copy_blueprint 当玩家说“把这片产线存成蓝图”时；id 来自 game_analyze_area / game_get_entities。',
        '一次最多 500 个；工具只保存文件，不建造、不粘贴，也不需要确认令牌。',
        '保存后告诉玩家文件名与位置；重名会自动加 (2)/(3)。',
      ],
      parameters: Type.Object({
        planetId: planetArg,
        entityIds: Type.Array(Type.Integer({ minimum: 1 }), { description: '要包含的建筑 id' }),
        name: Type.Optional(Type.String({ description: '蓝图文件名（不含 .txt），默认 IcarusII-时间戳' })),
      }),
      timeoutMs: 20000,
    }),
    gameTool(host, {
      name: 'game_analyze_area',
      label: '产线分析',
      description:
        '按空间范围聚合产线：各类建筑数量、各配方的机器数与装机产能、耗电、传送带/分拣器/物流塔数量、' +
        '涉及电网的供需。用于回答“这片区域是什么产线、规模多大、吃多少电”。' +
        '只给装机口径；实际产出请再用 game_get_production。',
      promptSnippet: '按范围聚合建筑、配方、装机产能与电力',
      promptGuidelines: [
        'Use game_analyze_area 当玩家问“这片产线是干什么的/多大/耗多少电”时。',
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
            x: Type.Number(),
            y: Type.Optional(Type.Number()),
            z: Type.Number(),
          }),
        ),
        all: Type.Optional(Type.Boolean({ description: '分析整颗星球，忽略 radius/center' })),
        includeEntities: Type.Optional(Type.Boolean({ description: '是否列出范围内建筑，默认 false' })),
        limit: Type.Optional(Type.Integer({ description: '列出的建筑上限，默认 40，最大 100' })),
      }),
      timeoutMs: 15000,
    }),
  ];

  return tools.filter((tool) => {
    const capability = TOOL_CAPABILITY[tool.name];
    return capability !== undefined && host.has(capability);
  });
}

registerGameToolsFactory(createGameTools);

/** `ready.payload.tools` 用：按能力返回本次连接实际可用的工具名。 */
export function toolNamesForCapabilities(capabilities: readonly string[]): string[] {
  // kb_* 是 sidecar 本地工具，不依赖 mod 能力；始终列出，未建立时返回 kb_unavailable。
  const game = Object.entries(TOOL_CAPABILITY)
    .filter(([, capability]) => capabilities.includes(capability))
    .map(([name]) => name);
  // 脚本工具是 sidecar 本地能力，始终注册（没有 game 工具时执行会明确报错）。
  return [...game, ...KB_TOOL_NAMES, ...WIKI_TOOL_NAMES, ...BLUEPRINT_TOOL_NAMES, SCRIPT_TOOL_NAME];
}
