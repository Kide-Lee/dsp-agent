/**
 * 返回字段的类型与说明（人工维护）。
 *
 * 为什么手写：mod 侧 C# 是命令式构造 `JsonValue`，没有 schema 可导出。
 * 自动提取只能拿到字段名和赋值表达式，拿不到类型和语义；类型靠表达式猜、
 * 说明靠字段名猜，都会把文档写成“看起来对”的假信息。所以这里人工写，
 * 生成器只负责把自动提取到的字段名与这份表对齐。
 *
 * 约定：
 *   - 键是工具名，值是该工具**顶层**返回字段；
 *   - 类型用 JSON 类型名：string / integer / number / boolean / object / array；
 *   - 字段与某个参数同名时，说明里写“同参数 xxx”，避免重复解释；
 *   - 只在特定分支出现的字段，说明里标出条件（例如“仅 veins=nodes”）。
 *
 * 新增返回字段时：先在 C# 里加，再补这里，然后运行生成器；
 * `test:tool-docs` 会检查 C# 顶层字段是否都在本表里。
 */

export type FieldNote = readonly [type: string, desc: string];

export const TOOL_FIELD_NOTES: Record<string, Record<string, FieldNote>> = {
  // ---------------------------------------------------------------- 只读概览

  game_get_overview: {
    saveName: ['string', '存档名；空字符串表示尚未进入存档'],
    saveKey: ['string', '当前存档的稳定标识；换档后变化，可用于判断上下文是否失效'],
    gameVersion: ['string', '游戏版本号'],
    gameTick: ['integer', '游戏 tick（存档逻辑时间）'],
    gameTimeSec: ['number', '游戏时间，单位秒（兼容旧字段）'],
    gameTimeDisplay: ['string', '游戏时间的可读文本（时分秒）'],
    paused: ['boolean', '游戏是否暂停'],
    planet: ['object', '当前行星：`id` / `name` / `type` / `factoryLoaded`'],
    star: ['object', '当前星系：`id` / `name` / `type`'],
    player: ['object', '玩家位置 `location`（经纬度）；机甲状态用 space_motion op=information'],
    mode: ['object', '存档模式：`peace` / `sandbox` / `combat` / `goalLevel`'],
    display: ['string', '一行摘要：`行星名 · 游戏时间`'],
  },


  game_get_planet: {
    planetId: ['integer', '同参数 `planetId`；省略时是当前行星 id'],
    name: ['string', '行星显示名'],
    type: ['string', '行星类型（枚举名）'],
    radius: ['number', '行星半径（米）'],
    factoryLoaded: ['boolean', '该行星的工厂是否已加载；false 时矿脉/建筑统计不完整'],
    veins: ['object', '按资源汇总的矿脉：资源名 → 数量/储量'],
    buildings: ['object', '按原型计数的建筑：原型名 → 数量'],
    veinGroupsAvailable: ['boolean', '`veins=groups` 时是否返回了矿脉群'],
    veinGroupsReason: [
      'string',
      '不可用原因：`vein_groups_not_ready`（尚未扫描）/ `planet_not_scanned`（未观察）',
    ],
    veinGroupCount: ['integer', '矿脉群数量（仅 veins=groups）'],
    veinGroups: ['array', '矿脉群：`groupIndex` / 资源 / 位置 / 节点数（仅 veins=groups）'],
    veinNodesAvailable: ['boolean', '`veins=nodes` 时是否返回了逐矿脉节点'],
    veinNodesReason: ['string', '不可用原因：`factory_not_loaded` / `vein_pool_empty`'],
    veinNodes: [
      'array',
      '逐矿脉节点：`id` / 资源 / 位置（仅 veins=nodes；分页用 veinOffset/veinLimit）',
    ],
    display: ['string', '一行摘要'],
  },


  game_get_research: {
    currentTechId: ['integer', '当前研究科技 id；0 表示没有在研'],
    current: ['object', '当前研究详情：名称 / 进度 / 剩余时间'],
    queue: ['array', '研究队列（科技 id / 名称 / 顺序）'],
    techSpeed: ['number', '研究速度倍率'],
    unlockedCount: ['integer', '已解锁科技数'],
    totalTechs: ['integer', '科技总数'],
    display: ['string', '一行摘要：在研科技与进度'],
  },


  // ---------------------------------------------------------------- 实体

  game_get_entity: {
    planetId: ['integer', '同参数 `planetId`'],
    entityId: ['integer', '同参数 `entityId`'],
    available: ['boolean', '工厂是否已加载；false 时看 `reason`'],
    reason: ['string', '不可用原因：`factory_not_loaded` / `not_found` / `empty_slot`'],
    protoId: ['integer', '建筑原型 id'],
    protoName: ['string', '建筑原型名'],
    iconTag: ['string', '图标短码（可直接写进 `<icon tag>`）'],
    category: ['string', '建筑类别：assembler / miner / belt / station …'],
    yaw: ['number', '朝向角（度）'],
    power: ['object', '该建筑的功率信息（需求/供给/满足率）'],
    stale: ['boolean', 'id 已被回收或与 expectProtoId 不匹配；true 时整条数据不可用'],
    expectProtoId: ['integer', '回显参数 `expectProtoId`，用于核对'],
    count: ['integer', '批量读取（entityIds）时的条数'],
    entities: ['array', '批量读取结果；默认只含 core 字段'],
    assembler: [
      'object',
      '装配/制造段：`recipeId` / `recipeName` / `recipeSeconds`（配方与单次耗时）、' +
        '`speedMultiplier`（速度倍率）、`craftsPerMin`（每分钟次数）、`working`（是否在运行）、' +
        '`replicating` / `extraSpeed`（增产与加成）、`productPerMin`（每分钟产量）、' +
        '`forceAccMode`（强制加速）、`inputs` / `outputs`（槽位数组，元素含 `itemId` / `name` / ' +
        '`iconTag` / `count`，输入还有 `needed` / `incServed`）、`powerSatisfactionPct`（供电满足率）、' +
        '`status`（缺料/没电/运行/待机）',
    ],
    miner: [
      'object',
      '采矿段：`productId` / `productName`（产出物品）、`productCount`（单次产出）、' +
        '`period`（周期）、`orePerMin`（每分钟产量）、`veinCount`（覆盖矿脉数）、' +
        '`totalVeinAmount`（剩余总储量）、`workstate`（工作状态）',
    ],
    inserter: [
      'object',
      '分拣器段：`grade`（等级）、`bidirectional`（是否双向）、`stage`、' +
        '`filterItemId` / `filterItemName`（过滤物品）、`pickTarget` / `insertTarget`（取/放目标 id）' +
        '与 `pickTargetName` / `insertTargetName`、`handItemId` / `handItemName` / `handItemCount` / ' +
        '`handItemInc`（手上物品）、`stackCount` / `canStack` / `stackInput` / `stackOutput`（堆叠）、' +
        '`delay`（延迟 tick）',
    ],
    belt: [
      'object',
      '传送带段：`itemsPerSec`（实际吞吐/秒）、`segLength`（段长）、`rangeFrom` / `rangeTo`（起止节点）、' +
        '`nodeIndex`、`pathId`（所属线路）、`pathCapacity`（线路容量）、' +
        '`pathNodeCount` / `pathCargoCount`（线路上节点/货物数）',
    ],
    storage: [
      'object',
      '仓储段：`slots`（格数）、`items`（物品数组，元素含 `itemId` / `name` / `iconTag` / `count`）、' +
        '`totalCount`（总物品数）',
    ],
    station: [
      'object',
      '物流塔段：`isCollector`（轨道采集器）、`isStellar`（星际塔）、' +
        '`warperCount` / `warperMaxCount`（翘曲器）、`workDroneCount` / `idleDroneCount`（无人机）、' +
        '`workShipCount` / `idleShipCount`（运输船）、`deliveryDrones` / `deliveryShips`（配送中）、' +
        '`warpEnableDistAu`（启用翘曲距离，AU）、`energy` / `energyMax` / `chargeKw` / `charging`（储能与充电）、' +
        '`slots`（槽位数组，元素含 `itemId` / `name` / `inTransit` / `max` / `localOrder` / ' +
        '`remoteOrder` / `localLogic` / `remoteLogic`）',
    ],
    components: ['object', '逐字段调试段（仅 fields 含 components，返回量大）'],
    location: ['object', '建筑坐标 `location`（经纬度），由 GameToolFormat.SetLocation 追加'],
    display: ['string', '一行摘要'],
  },


  game_get_planets: {
    available: ['boolean', '银河数据是否已加载'],
    observeLevel: ['integer', '宇宙探索等级；决定未观察星球是否披露矿脉'],
    planetCount: ['integer', '行星总数'],
    observedCount: ['integer', '已观察行星数'],
    factoryCount: ['integer', '已建工厂的行星数'],
    planets: [
      'array',
      '行星列表：`id` / `name` / 类型 / 轨道 / `factoryLoaded` / `hasFactory` / ' +
        '`observed` / `veinsHidden`（未观察时不给矿脉）',
    ],
    note: ['string', '给模型的说明：未观察星球只给基础轨道信息'],
    display: ['string', '一行摘要：行星数 · 已观察 · 有工厂'],
  },

  game_get_logistics: {
    available: ['boolean', '银河数据是否已加载'],
    itemFilter: ['string', '回显参数 `itemFilter`；未传时为 null'],
    totalStations: ['integer', '已加载工厂里的物流塔总数'],
    matchedStations: ['integer', '命中过滤的塔数'],
    offset: ['integer', '同参数 `offset`'],
    limit: ['integer', '同参数 `limit`'],
    items: ['array', '按物品聚合的供给/需求：`itemId` / `name` / 供给行星 / 需求行星'],
    stations: ['array', '站点明细：星球 / 塔类型 / 槽位方向 / 运输船 / 无人机 / 翘曲器'],
    display: ['string', '一行摘要：匹配塔数 · 物品种数'],
  },

  game_trace_line: {
    available: ['boolean', '工厂是否已加载，或 seed entityId 是否有效'],
    planetId: ['integer', '同参数 `planetId`'],
    seed: ['integer', '起点建筑 id（回显）'],
    depthLimit: ['integer', '实际使用的最大跳数'],
    nodeCount: ['integer', '节点数'],
    edgeCount: ['integer', '边数'],
    truncated: ['boolean', '达到深度/节点上限；调大 depth / maxNodes 继续'],
    nodes: ['array', '节点：`id` / 名称 / 类别 / 星系 / 行星 / 经纬度 / 深度'],
    edges: ['array', '有向边：`from` → `to`（传送带/分拣器连接）'],
    display: ['string', '一行摘要：节点数 · 连接数'],
  },

  // ---------------------------------------------------------------- 进度 / 截图 / 分析

  game_get_progress: {
    signals: ['object', '阶段判定信号：是否已解锁关键科技/建筑'],
    stage: ['string', '派生阶段（电磁 / 炼油 / 钛 / 星际物流 / 翘曲 / 戴森球…）'],
    stageNote: ['string', '提示：阶段是派生结果，回答时要说明依据'],
    unlockedTechs: ['integer', '已解锁科技数'],
    tutorials: ['integer', '已完成教程数'],
    currentTechId: ['integer', '当前研究科技 id'],
    currentTechName: ['string', '当前研究科技名'],
    techSpeed: ['number', '研究速度倍率'],
    queue: ['array', '研究队列'],
    milestoneCount: ['integer', '里程碑达成数'],
    recentMilestones: ['array', '最近的里程碑'],
    display: ['string', '一行摘要'],
  },

  game_capture_view: {
    width: ['integer', '图像宽度（像素）'],
    height: ['integer', '图像高度（像素）'],
    available: ['boolean', '游戏/相机是否就绪'],
    reason: ['string', '不可用原因（例如 `camera_unavailable`）'],
    bytes: ['integer', 'JPEG 字节数'],
    view: ['string', '实际使用的机位：player / topdown / orbit'],
    camera: ['object', '机位参数（距离/俯角/方位角/视场角/中心点）'],
    visibleEntities: ['array', '可见建筑的屏幕坐标：编号 / 名称 / x / y（与图片标注对应）'],
    annotated: ['boolean', '是否在图上画了编号标注'],
    legend: ['array', '标注图例：编号 → 建筑名'],
    display: ['string', '一行摘要'],
  },


  game_render_layout: {
    planetId: ['integer', '同参数 `planetId`'],
    available: ['boolean', '工厂是否已加载'],
    grid: ['array', '字符网格行；每个字符代表一个 cellSize 米方格'],
    width: ['integer', '网格宽度（格）'],
    height: ['integer', '网格高度（格）'],
    cellSize: ['number', '每格米数'],
    origin: ['object', '网格原点坐标 `location`（经纬度）'],
    legend: ['array', '字符图例：字符 → 建筑类别'],
    orientation: ['string', '方向约定：上=+Z，左=-X'],
    display: ['string', '一行摘要'],
  },

  // ---------------------------------------------------------------- 写操作

  game_copy_blueprint: {
    saved: ['boolean', '是否成功写出蓝图文件'],
    error: ['string', '失败原因（例如 `factory_not_loaded`）'],
    source: ['string', '蓝图来源：`copied`（游戏复制逻辑）'],
    path: ['string', '蓝图文件的完整路径'],
    relPath: ['string', '相对蓝图根的路径（默认 `IcarusII/文件名`）'],
    dir: ['string', '传入的目录参数（回显）'],
    dirRel: ['string', '相对蓝图根的实际目录；根目录时为空字符串'],
    fileName: ['string', '文件名（含 `.txt`）'],
    buildings: ['integer', '蓝图包含的建筑数'],
    areas: ['integer', '蓝图包含的区域数'],
    name: ['string', '蓝图标题（来自游戏蓝图元数据）'],
    author: ['string', '蓝图作者'],
    version: ['string', '蓝图自定义版本'],
    attributes: ['string', '蓝图外部字段（游戏原始字符串）'],
    description: ['string', '蓝图说明'],
    iconItemId: ['integer', '蓝图图标使用的物品 id'],
    width: ['integer', '蓝图拖拽框宽（格）'],
    height: ['integer', '蓝图拖拽框高（格）'],
    display: ['string', '一行摘要'],
  },

  game_place_marker: {
    display: ['string', '一行确认摘要'],
  },

  game_open_panel: {
    display: ['string', '一行确认摘要'],
  },

  game_set_research: {
    display: ['string', '一行确认摘要'],
  },

  game_drop_item: {
    display: ['string', '一行确认摘要'],
  },

  game_undo_last_write: {
    display: ['string', '撤销结果；最近一步不可撤销时会明确说明'],
  },

  // ---------------------------------------------------------------- 产线写

  game_set_recipe: {
    display: ['string', '一行确认摘要'],
  },

  game_set_assembler_batch: {
    display: ['string', '一行确认摘要（成功台数 / 跳过台数）'],
  },

  game_take_output: {
    buildingId: ['integer', '生产建筑 id'],
    taken: ['integer', '本次取出的成品总数'],
    remaining: ['integer', '取出后仍留在建筑里的数量'],
    items: ['array', '取出的物品明细：`id` / `name` / `count`'],
    undoable: ['boolean', '固定 false：取成品不可撤销，且会占用撤销栈顶'],
    display: ['string', '一行确认摘要'],
  },

  game_feed_input: {
    buildingId: ['integer', '生产建筑 id'],
    itemId: ['integer', '同参数 `itemId`'],
    itemName: ['string', '物品名'],
    fed: ['integer', '实际放进输入缓冲的数量'],
    served: ['integer', '该输入槽当前已满足的需求量'],
    undoable: ['boolean', '固定 true：投料可撤销'],
    display: ['string', '一行确认摘要'],
  },

  // ---------------------------------------------------------------- 仓储

  game_storage_set_filter: {
    buildingId: ['integer', '存储建筑 id'],
    itemId: ['integer', '同参数 `itemId`；0 表示清除过滤器'],
    itemName: ['string', '物品名；清除时为「清除」'],
    changedGrids: ['integer', '实际改动的格数'],
    slots: ['integer', '该存储的总格数'],
    undoable: ['boolean', '固定 true'],
    display: ['string', '一行确认摘要'],
  },

  game_storage_set_limit: {
    buildingId: ['integer', '存储建筑 id'],
    limit: ['integer', '同参数 `limit`（允许使用的格数）'],
    slots: ['integer', '该存储的总格数'],
    bans: ['integer', '被禁用的格数'],
    undoable: ['boolean', '固定 true'],
    display: ['string', '一行确认摘要'],
  },

  game_storage_move: {
    sourceId: ['integer', '同参数 `sourceId`'],
    targetId: ['integer', '同参数 `targetId`'],
    itemId: ['integer', '同参数 `itemId`'],
    itemName: ['string', '物品名'],
    requested: ['integer', '请求移动的数量'],
    moved: ['integer', '实际移动的数量'],
    remaining: ['integer', '因空间/库存不足没移动的数量'],
    undoable: ['boolean', '固定 true'],
    display: ['string', '一行确认摘要'],
  },

  game_storage_deposit: {
    buildingId: ['integer', '存储建筑 id'],
    itemId: ['integer', '同参数 `itemId`'],
    itemName: ['string', '物品名'],
    requested: ['integer', '请求存入的数量'],
    stored: ['integer', '实际存入的数量'],
    remaining: ['integer', '没存进去、仍留在背包的数量'],
    undoable: ['boolean', '固定 true'],
    display: ['string', '一行确认摘要'],
  },

  game_storage_withdraw: {
    buildingId: ['integer', '存储建筑 id'],
    itemId: ['integer', '同参数 `itemId`'],
    itemName: ['string', '物品名'],
    requested: ['integer', '请求取出的数量'],
    taken: ['integer', '实际取出的数量'],
    remaining: ['integer', '没取出、仍留在存储里的数量'],
    undoable: ['boolean', '固定 true'],
    display: ['string', '一行确认摘要'],
  },

  // ---------------------------------------------------------------- 辅助

  game_tether_add: {
    kind: ['string', '指示类型：`planet` / `vein` / `building`'],
    planetId: ['integer', '目标所在行星 id'],
    veinId: ['integer', '矿脉 id（仅 kind=vein）'],
    buildingId: ['integer', '建筑 id（仅 kind=building）'],
    name: ['string', '目标名称（显示用）'],
    subtitle: ['string', '副标题'],
    description: ['string', '描述文本'],
    iconLabel: ['string', '图标短码或 `#id`'],
    display: ['string', '一行确认摘要'],
  },

  game_tether_list: {
    items: ['array', '当前方位指示：类型 / 行星 / 建筑或矿脉 id / 名称'],
    display: ['string', '一行摘要：当前有几个指示'],
  },

  game_todo_templates: {
    templates: ['array', '目标模板：`id` / `name` / `determinator` / `level` / `parentId` / `params`'],
    count: ['integer', '模板数量'],
    display: ['string', '一行摘要'],
  },

  game_todo_list: {
    goals: [
      'array',
      'AI 创建的目标：`groupId` / `goalId` / `group` / `title` / `templateId` / ' +
        '`current` / `target` / `stage`',
    ],
    count: ['integer', '目标数量'],
    goalPanelOff: ['boolean', '目标面板确实没有显示；true 时提醒玩家打开'],
    todo: ['boolean', '该行是轮内待办（group 固定为第一组）'],
    display: ['string', '一行摘要'],
  },

  game_todo_add: {
    count: ['integer', '新增的目标数'],
    goalPanelOff: ['boolean', '目标面板确实没有显示（自由探索且模组未接管），需要提醒玩家'],
    display: ['string', '一行确认摘要'],
  },

  game_todo_remove: {
    removed: ['integer', '删除的目标数'],
    display: ['string', '一行确认摘要'],
  },

  game_todo_write: {
    group: ['string', '待办所在的原生目标分组名（固定为第一组）'],
    todos: [
      'array',
      '完整列表：`id` / `content` / `status`（pending / in_progress / completed；玩家跳过的是 skipped）',
    ],
    count: ['integer', '条目数'],
    counts: ['object', '`pending` / `inProgress` / `completed` / `skipped`'],
    goalPanelOff: ['boolean', '目标面板确实没有显示；true 时提醒玩家打开'],
    display: ['string', '一行确认摘要'],
  },

  space_region: {
    type: ['string', '区域类型（star_system / star / planet / power_grid / factory / df_hive / df_base / dyson_sphere / world_box / planet_patch / blueprint）'],
    target: ['string', '解析后的对象描述，如「恒星系 天仓五（#3）」'],
    planetId: ['integer', '行星 id（行星类区域）'],
    starId: ['integer', '恒星 id（恒星系 / 恒星 / 戴森球 / 巢穴）'],
    networkId: ['integer', '电网 id（power_grid / factory）'],
    region: ['object', '几何区域：`frame` + `latMin`/`latMax`/`lonStart`/`lonSpan` 或 `min`/`max`'],
    members: [
      'array',
      'list 的成员行：`kind` / `id` / `name` / `planetId` / `category` / `recipeId` / `recipeName` / ' +
        '`speedMultiplier` / `recipeSeconds` / `yaw` / `location`（经纬度）或 `world`（世界坐标）',
    ],
    items: [
      'array',
      '存量或流量行：`id` / `name` / `iconTag` / `amount` / `net` / `stored` / `minutesOfStock` / ' +
        '`producedPerMin` / `consumedPerMin`（统计类才有）',
    ],
    stat: ['object', '统计口径：`window` / `unit` / `basis` / `windowNative`；存量时只有 `kind=stock` + `unit=count`'],
    statsEmpty: ['boolean', '统计桶为空（刚读档或暂停）；true 不等于没有产线'],
    hint: ['string', '口径或口径受限的说明'],
    dyson: [
      'object',
      '戴森球结构量：`sailCount` / `nodeCount` / `frameCount` / `shellCount` / `structurePoints` / ' +
        '`cellPoints` / `swarmGenKw` / `layerGenKw`。恒星系、恒星与戴森球区域会带这一块',
    ],
    matter: ['object', '黑雾物质：`matterProduction` / `matterConsumption` / `statWindowNative` / `statPeriod`；基地另有 `matterGenPerMin` / `energyUsePerMin`'],
    structure: ['object', '巢穴结构计数：`cores` / `nodes` / `connectors` / `replicators` / `gammas` / `turrets` / `units` / `relays` / `tinders`'],
    power: [
      'object',
      '电力：`demandKw` / `servedKw` / `capacityKw` / `storedKwh` / `accumulatorKw` / `satisfactionPct` / ' +
        '`consumers` / `generators` / `nets`。恒星系另有 `dysonGenKw`、`planets`',
    ],
    parents: ['array', '上级链：每层 `type` / `target`（+ `starId` / `planetId`）'],
    location: ['object', '位置：恒星给 `world`；行星/电网/工厂给 `local` / 经纬度'],
    information: ['object', '静态信息，随类型不同：恒星给质量/光谱/光度；行星给轨道与自转；戴森球给 `layers` / `orbits`；蓝图给作者/版本/图标'],
    buildings: ['array', '蓝图内的建筑：`id` / `name` / `iconTag` / `count`'],
    blueprint: ['object', '蓝图头信息（同 information 的蓝图分支）'],
    recipeBuildings: ['integer', '蓝图里带配方的建筑台数（理论流量的计算基数）'],
    planetCount: ['integer', '参与统计的行星数（世界区域 / 恒星系）'],
    note: ['string', '备忘录正文或范围受限说明'],
    total: ['integer', '总条数（分页前的数量）'],
    truncated: ['boolean', '是否还有下一页'],
    nextOffset: ['integer', '下一页偏移；-1 表示没有更多'],
    partial: ['boolean', '结果不完整（有行星未加载或只扫了部分区域）'],
    display: ['string', '一行确认摘要'],
  },

  space_point: {
    type: ['string', '定点类型（building / vein / vegetation / df_space_building / df_ground_building / world_position / planet_position）'],
    target: ['string', '解析后的对象描述'],
    name: ['string', '对象名（建筑原型名、矿物名等）'],
    planetId: ['integer', '所在行星 id'],
    starId: ['integer', '所在恒星 id'],
    entityId: ['integer', '建筑实体 id'],
    veinId: ['integer', '矿脉 id'],
    vegeId: ['integer', '植被 id'],
    enemyId: ['integer', '黑雾实体 id'],
    owned: ['array', 'list 的下辖动点：`kind` / `id` / `subId` / `name`'],
    items: ['array', '存量或流量行，字段同 space_region 的 items'],
    stat: ['object', '统计口径；存量时只有 `kind=stock` + `unit=count`'],
    hint: ['string', '口径说明；矿脉输出与理论值会注明 basis=installed'],
    matter: ['object', '黑雾建筑的物质与能量'],
    power: ['object', '电力：`demandKw` / `servedKw` / `capacityKw` / `storedKwh` / `accumulatorKw` / `satisfactionPct` / `nets`'],
    parents: ['array', '归属：`type`（planet / factory / df_base / df_hive / none）+ 相关 id'],
    planet: ['object', '行星坐标：`planetId` / `planetName` / `latitude` / `longitude` / `altitude`'],
    world: ['object', '世界坐标：`x` / `y` / `z`（世界坐标类定点）'],
    note: ['string', '全息信标的备忘录正文'],
    total: ['integer', '总条数'],
    truncated: ['boolean', '是否还有下一页'],
    nextOffset: ['integer', '下一页偏移；-1 表示没有更多'],
    display: ['string', '一行确认摘要'],
  },

  space_motion: {
    type: ['string', '动点类型（player / construction_drone / delivery_courier / station_drone / station_ship / dyson_rocket / combat_drone / combat_fleet / df_space_unit / df_ground_unit / trash）'],
    target: ['string', '解析后的对象描述'],
    name: ['string', '对象名'],
    planetId: ['integer', '关联行星 id（在行星上或所属行星）'],
    starId: ['integer', '关联恒星 id'],
    owned: ['array', '玩家名下的动点：`kind` / `id` / `name`'],
    items: ['array', '携带物品或存量：`id` / `name` / `iconTag` / `amount` / `stored`'],
    stat: ['object', '存量口径：`kind=stock` + `unit=count`'],
    hint: ['string', '口径说明；运输类取不到货时会说明货在别处'],
    parents: ['array', '归属：`type`（player / station / silo / fleet / df_hive / df_base / none）+ 相关 id'],
    location: [
      'object',
      '当前位置快照：`gameTick` / `speed`；在行星上给 `latitude` / `longitude` / `altitude` / `planetId`，' +
        '在宇宙中给 `position`（x/y/z）',
    ],
    information: [
      'object',
      '状态：玩家给 `movementState` / `warpState` / `coreEnergy` / `shieldEnergy` / `shieldPct` / `reactorEnergy`；' +
        '运输类给 `prototype` / `warpState` / `warperCount`；战斗类给 `behavior` / `hpShortage`；' +
        '黑雾给 `stateFlags`；垃圾块给 `life` / `landPlanetId` / `nearStarId`',
    ],
    total: ['integer', '总条数'],
    truncated: ['boolean', '是否还有下一页'],
    nextOffset: ['integer', '下一页偏移；-1 表示没有更多'],
    display: ['string', '一行确认摘要'],
  },
};
