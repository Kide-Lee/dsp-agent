// 小型运载火箭 30/min，外部原料全部用珍奇矿
// 编译器负责：配方展开、机器数、模块摆放、连接、校验、出图
// 本文件是 DSL v0 的目标语法示例，M1 编译器落地后可编译

target('小型运载火箭', 30);

// 外部输入：基础矿 + 珍奇矿。列在这里的东西不再向上游展开。
raw(
  // 基础
  '铁矿', '铜矿', '硅石', '钛矿', '石矿', '煤矿',
  // 珍奇
  '单极磁石', '光栅石', '刺笋结晶', '可燃冰', '金伯利矿', '分形硅石', '有机晶体',
);

options({
  belt: '极速传送带',
  sorter: '集装分拣器',
  assembler: '制造台 Mk.III',
  smelter: '位面熔炉',
  chemical: '化工厂',
  lab: '研究站',
  station: '行星内物流运输站',
  power: '卫星配电站',
  proliferator: 3,

  recipePolicy: 'rare-first', // 能走珍奇配方的中间产物都走珍奇配方

  latitude: 0,                // 赤道紧凑；要贴高纬改成 moduleSpacing: 'latitude-safe'
  elevation: 1,               // 最多用 1 层高架
  allowVerticalBelt: false,   // 不用原地垂直带（省超级磁场发生器）
  aspect: 2.5,
  maxWidth: 200,
  maxHeight: 120,
});

// 总装三件套：量子芯片、戴森球组件、氘核燃料棒
const 量子芯片 = module('量子芯片', { machines: 'auto' });
const 戴森球组件 = module('戴森球组件', { machines: 'auto' });
const 氘核燃料棒 = module('氘核燃料棒', { machines: 'auto' });

const 总装 = module('小型运载火箭', { machines: 'auto' });
connect(量子芯片.out, 总装.in);
connect(戴森球组件.out, 总装.in);
connect(氘核燃料棒.out, 总装.in);

// 上游模块由编译器按 BOM 自动补齐；这里只对关键链条分组摆放
group('芯片链', [量子芯片], { place: 'west' });
group('组件链', [戴森球组件], { place: 'center' });
group('燃料链', [氘核燃料棒], { place: 'east' });

station('原料站', { kind: 'raw', side: 'west' });
station('成品站', { kind: 'product', side: 'east' });

constraint('原料入口在左侧');
constraint('成品出口在右侧');
