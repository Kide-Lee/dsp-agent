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
export const CAPABILITY_GAME_DROP = 'game.drop.v1';
export const CAPABILITY_GAME_BUILD = 'game.build.v1';
export const CAPABILITY_GAME_STORAGE = 'game.storage.v1';
export const CAPABILITY_GAME_TETHER = 'game.tether.v1';
export const CAPABILITY_GAME_TODO = 'game.todo.v1';
export const CAPABILITY_GAME_TODO_WRITE = 'game.todo.write.v1';

export function isWriteToolName(name: string): boolean {
  return (WRITE_TOOL_NAMES as readonly string[]).includes(name);
}

export const WRITE_TOOL_NAMES = [
  'game_place_marker',
  'game_open_panel',
  'game_set_research',
  'game_drop_item',
  'game_set_recipe',
  'game_set_assembler_batch',
  'game_storage_set_filter',
  'game_storage_set_limit',
  'game_storage_move',
  'game_storage_deposit',
  'game_storage_withdraw',
  'game_feed_input',
  'game_tether_add',
  'game_tether_clear',
  'game_todo_add',
  'game_todo_remove',
  'game_take_output',
  'game_undo_last_write',
  'game_copy_blueprint',
  'mods_config_set',
  'mods_adapter_create',
  'mods_adapter_delete',
  'ui_click',
] as const;

/**
 * 写工具的 risk 分级（与各 spec 的 `risk` 一致）。
 *
 * 权限档位用它来判断脚本级确认的最高风险：
 *   - risk 1：可逆、无存档副作用（标记/面板/方位/撤销）
 *   - risk 2：改变存档状态
 *
 * 稳定导出给 `script.ts`；新增写工具时两边都要登记。
 */
const WRITE_TOOL_RISK: Record<string, 1 | 2> = {
  game_place_marker: 1,
  game_open_panel: 1,
  game_set_research: 2,
  game_drop_item: 2,
  game_set_recipe: 2,
  game_set_assembler_batch: 2,
  game_storage_set_filter: 2,
  game_storage_set_limit: 2,
  game_storage_move: 2,
  game_storage_deposit: 2,
  game_storage_withdraw: 2,
  game_feed_input: 2,
  game_tether_add: 1,
  game_tether_clear: 1,
  game_todo_add: 2,
  game_todo_remove: 2,
  game_take_output: 2,
  game_undo_last_write: 1,
  game_copy_blueprint: 1,
  mods_config_set: 1,
  mods_adapter_create: 2,
  mods_adapter_delete: 2,
  ui_click: 2,
};

/** 未知写工具按 risk 2 处理（更保守，仍会要求确认）。 */
export function writeToolRisk(name: string): 1 | 2 {
  return WRITE_TOOL_RISK[name] ?? 2;
}

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
      name: 'game_place_marker',
      label: '放置标记',
      description:
        '在玩家当前所在的行星上放置一个定位标记，效果与行星视图右键相同。' +
        '用 latitude/longitude 指定位置（可从 game_get_entity 的 location 读取）。' +
        '标记会在地图上显示光柱，抵达附近或离开行星后由游戏自动清除。写操作：执行前玩家必须确认。',
      promptGuidelines: [
        '玩家明确要求“在这里做个标记/标个点”时使用。',
        '坐标必须来自游戏数据或玩家给出；不要凭截图猜坐标。优先使用 game_get_entity 返回的 location.latitude/longitude。',
        '只能标记玩家当前所在的行星；玩家要求标记其他行星时先说明限制。',
      ],
      parameters: Type.Object({
        latitude: Type.Number({ description: '纬度（-90..90）' }),
        longitude: Type.Number({ description: '经度（-180..180）' }),
      }),
      timeoutMs: 8000,
      risk: 1,
    }),
    writeTool(host, {
      name: 'game_open_panel',
      label: '打开面板',
      description:
        '打开游戏内面板：inventory（背包）、tech（科技树）、starmap（星图）、' +
        'marker（标记）、mecha（机甲）、replicator（复制器）、statistics（统计）、blueprint（蓝图）。' +
        '写操作：执行前玩家必须确认。',
      promptGuidelines: [
        '玩家明确要求打开某个游戏面板时使用。',
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
        '执行前玩家必须确认。已完成的科技会被拒绝；科技必须未完成且前置已满足。' +
        'techId 可以是科技 id 或名称。',
      promptGuidelines: [
        '玩家明确要求切换研究目标时使用。',
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
      name: 'game_drop_item',
      label: '丢弃物品',
      description:
        '把背包里的物品丢到当前行星地面。写操作，风险 2：执行前玩家必须确认。' +
        '丢出的物品会变成可拾取的垃圾，需玩家手动拾回；不进入撤销栈。' +
        'itemId 可以是物品 id 或名称；count 默认 1，一次最多 500。',
      promptGuidelines: [
        '只在玩家明确要求“丢掉/扔了”某个物品时使用。',
        '调用前先用 space_motion op=storage type=player 确认背包里有该物品和数量；不要丢掉关键材料。',
        '丢弃后不进入撤销栈，必须明确告诉玩家“需要手动拾回”。',
        '如果玩家拒绝确认，不要再次调用。',
      ],
      parameters: Type.Object({
        itemId: Type.Union([Type.Integer(), Type.String()], {
          description: '物品 id 或名称',
        }),
        count: Type.Optional(
          Type.Integer({ minimum: 1, maximum: 500, description: '丢弃数量，默认 1，最大 500' }),
        ),
      }),
      timeoutMs: 8000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_set_recipe',
      label: '更换配方',
      description:
        '把一台生产建筑（制造台/熔炉/化工厂/研究站等）的配方改为指定配方。写操作，风险 2：' +
        '执行前玩家必须确认。会先按游戏 UI 的“重置配方”把机器内原料/产物退回背包，再切换配方；' +
        '撤销只恢复配方，不把退回背包的物品塞回机器。recipeId=0 表示清空配方。',
      promptGuidelines: [
        '玩家明确要求改某一台建筑的配方时使用；先用 game_get_entity 确认它是生产建筑。',
        'recipeId 先用 kb_search/kb_get 或 game_get_entity 的 recipeId 确认；不要编造。',
        '会先退料；如果玩家不想让物品回背包，先说明。撤销只恢复配方。',
      ],
      parameters: Type.Object({
        buildingId: Type.Integer({ description: '生产建筑 entityId' }),
        recipeId: Type.Integer({ description: '配方 id；0 = 清空配方' }),
      }),
      timeoutMs: 10000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_set_assembler_batch',
      label: '批量配方',
      description:
        '一次修改多台生产建筑的配方（最多 20 台），只弹一次确认、只压一层撤销。写操作，风险 2：' +
        '执行前玩家必须确认。每台都会先退回机器内原料/产物再切换；任何一台预检失败则整批不执行。',
      promptGuidelines: [
        '玩家要求“这几台/这一排都改成同一个配方”时使用；先用 space_region op=list 拿到 id。',
        '一次最多 20 台；超过时分批，不要一次塞太多。',
        '返回 changed/skipped/items；如果某台不匹配，整批会失败并说明原因。',
      ],
      parameters: Type.Object({
        selections: Type.Array(
          Type.Object({
            buildingId: Type.Integer({ description: '生产建筑 entityId' }),
            recipeId: Type.Integer({ description: '配方 id；0 = 清空配方' }),
          }),
          { minItems: 1, maxItems: 20, description: '最多 20 台；同一 buildingId 不能重复' },
        ),
      }),
      timeoutMs: 15000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_storage_set_filter',
      label: '存储过滤',
      description:
        '设置一台存储建筑（箱子/储液罐/物流站仓储）的物品过滤器；itemId=0 清除过滤器。写操作，风险 2：' +
        '执行前玩家必须确认。已占用且物品不同的格子会跳过，不会替换物品。',
      promptGuidelines: [
        '玩家要求“这个箱子只放某物品/不要放某物品”时使用；先用 game_get_entity 确认 storage 段。',
        'itemId 用 kb_search/kb_get 或已知物品 id；0 表示清除过滤器。',
        '只支持普通箱子/储液罐/物流站仓储；燃料/弹药等专用存储不支持。',
      ],
      parameters: Type.Object({
        buildingId: Type.Integer({ description: '存储建筑 entityId' }),
        itemId: Type.Integer({ description: '物品 id；0 = 清除过滤器' }),
      }),
      timeoutMs: 10000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_storage_set_limit',
      label: '存储上限',
      description:
        '设置一台存储建筑的容量上限（允许使用的格数）。写操作，风险 2：执行前玩家必须确认。' +
        'limit 是允许使用的格数（0..总格数），不是禁止格数。',
      promptGuidelines: [
        '玩家要求“这个箱子只用前 N 格”时使用；先用 game_get_entity 看 storage.slots。',
        'limit 是允许格数，范围 0..slots。',
        '只支持普通箱子/储液罐/物流站仓储。',
      ],
      parameters: Type.Object({
        buildingId: Type.Integer({ description: '存储建筑 entityId' }),
        limit: Type.Integer({ description: '允许使用的格数，0..总格数' }),
      }),
      timeoutMs: 10000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_storage_move',
      label: '移库',
      description:
        '把指定物品从一台存储建筑移动到另一台存储建筑。写操作，风险 2：执行前玩家必须确认。' +
        '先预检目标容量/过滤器；目标放不下时整笔不执行。源不足时移动可用的部分并返回 remaining。',
      promptGuidelines: [
        '玩家要求“把这箱铜搬到那箱”时使用；先用 game_get_entity 确认两端 storage 段和距离。',
        'sourceId/targetId 都是建筑 entityId；itemId 用已知 id；count 是请求数量。',
        '目标空间不足会整笔失败；源不足只移动可用部分，remaining 表示没移动的数量。',
      ],
      parameters: Type.Object({
        sourceId: Type.Integer({ description: '源存储建筑 entityId' }),
        targetId: Type.Integer({ description: '目标存储建筑 entityId' }),
        itemId: Type.Integer({ description: '物品 id' }),
        count: Type.Integer({ description: '请求移动数量' }),
      }),
      timeoutMs: 15000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_storage_deposit',
      label: '存入存储',
      description:
        '把背包里的物品放进一台存储建筑（箱子/储液罐/物流站仓储）。写操作，风险 2：执行前玩家必须确认。' +
        '存储满时只放入能放下的部分，剩余会退回背包。',
      promptGuidelines: [
        '玩家要求“把这些东西放进箱子”时使用；先用 game_get_entity 确认 storage 段。',
        'itemId/count 必填；存储满时返回 remaining，剩余仍在背包。',
      ],
      parameters: Type.Object({
        buildingId: Type.Integer({ description: '存储建筑 entityId' }),
        itemId: Type.Integer({ description: '物品 id' }),
        count: Type.Integer({ description: '放入数量' }),
      }),
      timeoutMs: 10000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_storage_withdraw',
      label: '从存储取出',
      description:
        '把一台存储建筑里的物品取到背包。写操作，风险 2：执行前玩家必须确认。' +
        '背包满时只取能放下的部分，剩余留在存储里。',
      promptGuidelines: [
        '玩家要求“从箱子里拿点东西”时使用；先用 game_get_entity 确认 storage 段。',
        'itemId/count 必填；背包满时返回 remaining，剩余仍在存储。',
      ],
      parameters: Type.Object({
        buildingId: Type.Integer({ description: '存储建筑 entityId' }),
        itemId: Type.Integer({ description: '物品 id' }),
        count: Type.Integer({ description: '取出数量' }),
      }),
      timeoutMs: 10000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_feed_input',
      label: '投料',
      description:
        '从背包把配方原料直接放进一台生产建筑的输入缓冲。写操作，风险 2：执行前玩家必须确认。' +
        '这条路径不经过分拣器/传送带；只接受当前配方需要的物品，最多 500 个。',
      promptGuidelines: [
        '玩家要求“把背包里的原料塞进这台机器”时使用；先用 game_get_entity 确认 assembler.recipeId/inputs。',
        'itemId 必须是当前配方 Items 里的输入；不要投成品或配方不需要的物品。',
        '它会直接写 served/incServed，绕过分拣器/传送带；确认卡会说明。',
      ],
      parameters: Type.Object({
        buildingId: Type.Integer({ description: '生产建筑 entityId' }),
        itemId: Type.Integer({ description: '配方输入物品 id' }),
        count: Type.Integer({ description: '投入数量，1..500' }),
      }),
      timeoutMs: 10000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_take_output',
      label: '取成品',
      description:
        '把一台生产建筑当前已产出的成品取到背包，保留配方。写操作，风险 2：执行前玩家必须确认。' +
        '背包满时只取能放下的部分，并返回剩余数量。',
      promptGuidelines: [
        '玩家要求“把这几台机器的成品收了”时使用；先用 game_get_entity 确认 assembler 段。',
        '只取 produced 成品，不会退原料；配方保留。',
        '背包满时会返回 remaining，不要把 remaining 当成已取走。',
      ],
      parameters: Type.Object({
        buildingId: Type.Integer({ description: '生产建筑 entityId' }),
      }),
      timeoutMs: 10000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_tether_add',
      label: '方位指示',
      description:
        '建立机甲到行星/建筑/矿脉的方位指示，可同时建立多个（最多 20 个）。本星球的建筑/矿脉各放一个原生定位标记（与行星视图右键相同的光柱），' +
        '抵达附近后标记由游戏清除；跨行星时先用原生 navigation.indicatorAstroId 指向其中一个目标行星（游戏一次只支持一个导航朝向），' +
        '落到该行星后自动改用定位标记。写操作，风险 1：执行前玩家必须确认。',
      promptGuidelines: [
        '玩家说“给这些建筑/星球/矿脉都做个方位指示”时使用；先用 space_region op=list 确认目标。',
        'kind=planet 时用 planetId（缺省当前星球）；kind=building 时用 buildingId，可选 planetId；kind=vein 时用 veinId，可选 planetId。',
        '本星球建筑/矿脉可同时显示多个定位标记；跨行星原生导航一次只指一个目标行星，不要声称所有跨行星目标都同时可见。',
        '标记在抵达附近后消失、卡片按钮恢复为未定位，这是正常行为。',
        'space_* 的 op=navigation 会报错并把调用引到这里：把它的 target 原样放进 space 参数即可，不要自己换算成 planetId/buildingId。',
        '动点（玩家/无人机/运输船/战舰等）会移动，方位指示是静态箭头，会直接拒绝；需要位置请调 space_motion op=location。',
      ],
      parameters: Type.Object({
        kind: Type.Optional(Type.String({
          enum: ['planet', 'building', 'vein'],
          description: 'planet / building / vein；用 space 时可省略',
        })),
        planetId: Type.Optional(Type.Integer({ description: '行星 id，缺省当前星球' })),
        buildingId: Type.Optional(Type.Integer({ description: 'kind=building 时必填' })),
        veinId: Type.Optional(Type.Integer({ description: 'kind=vein 时必填' })),
        space: Type.Optional(Type.Object({
          kind: Type.Optional(Type.String({ description: 'region / point / motion' })),
          type: Type.Optional(Type.String({ description: '空间变量类型，同 space_region / space_point 的 type' })),
          target: Type.Optional(Type.Union([Type.String(), Type.Object({})], {
            description: '与 space_* 的 target 相同',
          })),
        }, {
          description:
            '用空间变量直接建方位指示：把 space_region / space_point 的 kind+type+target 原样放进来。' +
            '区域会折到它的落点行星或首座建筑，定点会折到建筑/矿脉/行星。',
        })),
      }),
      timeoutMs: 8000,
      risk: 1,
    }),
    writeTool(host, {
      name: 'game_tether_clear',
      label: '清除方位',
      description: '清除本 mod 建立的所有方位指示（导航朝向和定位标记）。写操作，风险 1：执行前玩家必须确认。',
      promptGuidelines: ['玩家说“取消方位/清除标记”时使用；只清本 mod 建立的目标。'],
      parameters: Type.Object({}),
      timeoutMs: 8000,
      risk: 1,
    }),
    writeTool(host, {
      name: 'game_tether_list',
      label: '方位列表',
      description: '列出当前方位指示（行星/建筑/矿脉及名称）。只读，不弹确认。',
      promptGuidelines: ['玩家问“现在连了哪些目标”时使用。'],
      parameters: Type.Object({}),
      timeoutMs: 5000,
      risk: 0,
    }),
    writeTool(host, {
      name: 'game_todo_add',
      label: '新增游戏目标',
      description:
        '在原生左侧“游戏目标”面板新增目标。写操作：执行前玩家必须确认。' +
        '先用 game_todo_templates 查原生 GoalProto 模板；templateId 必填，' +
        'title 是显示文案，params 可覆盖原生 DeterminatorParams。目标完成判定完全复用游戏原生 GoalDeterminator。',
      promptGuidelines: [
        '玩家明确要求“给我一个目标/待办/采集 N 个/解锁科技”等目标时使用。',
        '必须先调用 game_todo_templates 选择真实存在的模板；不要编造 templateId 或判断器名。',
        'title 要写清楚目标；itemId/count 只用于模板参数确实是 item/count 的常见情况，其他模板用 params 按模板原参数顺序覆盖。',
        '目标会出现在游戏左侧原生目标面板（玩家选“自由探索”时也显示，只列 AI 目标）；不要重复添加同一个目标。',
        '如果结果里 goalPanelOff=true，说明目标面板确实没有显示（玩家选了“自由探索”且模组未接管显示）；目标已经创建但玩家看不到，必须明确提醒玩家打开目标面板设置，把“目标建议”改为“关键节点”或“事无巨细”，不要声称目标已经显示。',
      ],
      parameters: Type.Object({
        group: Type.Optional(Type.String({ description: '分组标题，默认“游戏目标”' })),
        goals: Type.Array(
          Type.Object({
            templateId: Type.Integer({ description: '原生 GoalProto 模板 id，来自 game_todo_templates' }),
            title: Type.Optional(Type.String({ description: '目标显示文案；留空用模板名' })),
            params: Type.Optional(Type.Array(Type.Number(), { description: '覆盖原生 DeterminatorParams' })),
            itemId: Type.Optional(Type.Integer({ description: '模板参数第 0 位为物品 id 时可填' })),
            count: Type.Optional(Type.Integer({ description: '模板参数第 1 位为目标数量时可填' })),
          }),
          { description: '本轮要新增的目标列表' },
        ),
      }),
      timeoutMs: 10000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_todo_remove',
      label: '删除游戏目标',
      description:
        '删除 AI 创建的原生游戏目标；groupId 删除整组，goalId 删除单个子目标。写操作：执行前玩家必须确认。',
      promptGuidelines: [
        '玩家明确要求删除目标时使用。',
        '先用 game_todo_list 找到 groupId/goalId；不要删除不是 AI 创建的原生目标。',
        '轮内待办（列表里 todo=true 的行）不用这个工具删除：改用 game_todo_write 提交去掉它的新列表。',
      ],
      parameters: Type.Object({
        groupId: Type.Optional(Type.Integer({ description: '删除整组目标' })),
        goalId: Type.Optional(Type.Integer({ description: '删除单个目标' })),
      }),
      timeoutMs: 8000,
      risk: 2,
    }),
    writeTool(host, {
      name: 'game_todo_write',
      label: '更新待办',
      description:
        '把当前任务清单整份写入游戏左侧目标面板的第一组“AI 待办”。' +
        '每次调用替换上一次的列表（空数组清空）；状态 pending / in_progress / completed，' +
        '完成项在面板里打勾并划线，玩家也可以随时跳过某一条。' +
        '本轮结束时 sidecar 会自动把清单从面板撤掉，下一轮要显示就重新写。' +
        '按只读权限登记：只写原生目标面板的临时组，不写存档、不消耗确认令牌；回合结束自动清空。',
      promptGuidelines: [
        '开始一项需要多步完成的工作前先写待办；一步就能做完的琐事不要用。',
        '每次更新都提交完整列表，不要只发变化项；做完一步就把它改成 completed。',
        '条目是一句可执行的短任务；同一列表里不要重复，最多 20 条。',
        '待办显示在游戏左侧目标面板，玩家跳过某条后不要反复重加，除非玩家要求。',
      ],
      parameters: Type.Object({
        todos: Type.Array(
          Type.Object({
            content: Type.String({ description: '任务描述，一句话' }),
            status: Type.Union(
              [Type.Literal('pending'), Type.Literal('in_progress'), Type.Literal('completed')],
              { description: 'pending（未开始）/ in_progress（进行中）/ completed（完成）' },
            ),
          }),
          { description: '完整任务列表，替换上一次；空数组清空' },
        ),
      }),
      timeoutMs: 5000,
      risk: 0,
    }),
    writeTool(host, {
      name: 'game_undo_last_write',
      label: '撤销上一步',
      description:
        '撤销最近一次由本 mod 执行的写操作（视角、标记、面板、研究、配方、存储、投料等）。' +
        '只保留一层撤销栈；最近一次写操作不可撤销（如丢物品/取成品/方位指示）时返回明确错误；' +
        '写操作，执行前玩家必须确认。',
      promptGuidelines: [
        '玩家说“撤销/恢复刚才那一步”时使用。',
        '只撤销最近一次，不要连续撤销多次；不可撤销的操作会返回错误。',
      ],
      parameters: Type.Object({}),
      timeoutMs: 8000,
      risk: 1,
    }),
  ];
}
