// IR -> 产线规划 -> 工厂布局（站连站）-> 蓝图 + 报告。
// M1 只支持一个 target；自定义 module/connect/group 先记警告，M2 起才参与布局。
import type { KnowledgeBase } from '../../kb/knowledge.js';
// @ts-expect-error planner 是纯 JS
import { planBlueprint } from '../planner.mjs';
import { DslError, type DslIr } from './ir.js';

export interface CompileOptions {
  name?: string;
  beltType?: number;
  insertType?: number;
  buildingTier?: 'base' | 'mk2' | 'mk3';
  gameVersion?: string;
}

export interface CompileResult {
  text: string;
  meta: Record<string, unknown> & { width?: number; height?: number; buildings?: number; machines?: number; report?: unknown; warnings?: string[] };
  warnings: string[];
  plan: unknown;
}

const TIER_BY_ASSEMBLER: Record<string, 'base' | 'mk2' | 'mk3'> = {
  '制造台': 'base',
  '制造台 Mk.I': 'base',
  '制造台 Mk.II': 'mk2',
  '制造台 Mk.III': 'mk3',
};

const BELT_BY_NAME: Record<string, 0 | 1 | 2> = {
  '黄带': 0, '普通传送带': 0,
  '绿带': 1, '高速传送带': 1,
  '蓝带': 2, '极速传送带': 2,
};

const SORTER_BY_NAME: Record<string, 0 | 1 | 2 | 3> = {
  '分拣器': 0,
  '高速分拣器': 1,
  '极速分拣器': 2,
  '集装分拣器': 3,
};

/** 编译 IR。形状错误抛 DslError（E1xx），布局失败抛带原文的错误。 */
export function compileIr(kb: KnowledgeBase, ir: DslIr, options: CompileOptions = {}): CompileResult {
  const target = ir.targets[0];
  if (!target) throw new DslError('E101', '没有 target');
  if (ir.targets.length > 1) throw new DslError('E102', 'M1 只支持一个 target；多目标在 M2 支持');
  const item = kb.resolveItem(target.item);
  if (!item) throw new DslError('E103', `知识库里找不到物品：${target.item}`);
  // M1 只实现了 target + 少量 options。其余语法宁可明确报错，也不能静默生成
  // 一张和玩家描述不符的蓝图。
  if (ir.raw.length) throw new DslError('E105', 'M1 暂不支持 raw(...)：外部原料清单尚未接入配方展开');
  if (ir.modules.length || ir.connections.length) {
    throw new DslError('E106', 'M1 暂不支持 module(...) / connect(...)：自定义模块与连接尚未接入布局');
  }
  if (ir.groups.length || ir.stations.length || ir.constraints.length) {
    throw new DslError('E107', 'M1 暂不支持 group(...) / station(...) / constraint(...)');
  }

  const warnings: string[] = [];
  const ignoredOptions = Object.keys(ir.options).filter((key) => !['assembler', 'belt', 'sorter'].includes(key));
  if (ignoredOptions.length) {
    warnings.push(`W504 以下 options 暂未参与布局：${ignoredOptions.join(', ')}`);
  }

  const buildingTier = ir.options.assembler
    ? (TIER_BY_ASSEMBLER[ir.options.assembler] ?? options.buildingTier ?? 'base')
    : (options.buildingTier ?? 'base');
  const beltType = options.beltType ?? BELT_BY_NAME[ir.options.belt ?? ''] ?? 2;
  const insertType = options.insertType ?? SORTER_BY_NAME[ir.options.sorter ?? ''] ?? 3;
  const plan = kb.plan(item.id, { rate: target.rate, buildingTier });
  const common = {
    name: options.name ?? `DSL-${item.name}-${target.rate}`,
    beltType,
    insertType,
    gameVersion: options.gameVersion,
  };
  let built;
  try {
    built = planBlueprint(plan, { ...common, layout: 'factory' });
  } catch (factoryError) {
    // 工厂布局只覆盖标准机器；精炼厂/化工厂/研究站等退回总线布局的端口模式。
    try {
      built = planBlueprint(plan, { ...common, layout: 'bus' });
      warnings.push('W506 工厂布局不适用于该配方，已改用总线布局');
    } catch (busError) {
      const first = factoryError instanceof Error ? factoryError.message : String(factoryError);
      const second = busError instanceof Error ? busError.message : String(busError);
      throw new DslError('E201', `布局失败：${first}；总线布局也失败：${second}`);
    }
  }
  const meta = built.meta as CompileResult['meta'];
  return { text: built.text, meta, warnings: [...warnings, ...(meta.warnings ?? [])], plan };
}
