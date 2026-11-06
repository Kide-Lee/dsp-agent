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

/** 编译 IR。形状错误抛 DslError（E1xx），布局失败抛带原文的错误。 */
export function compileIr(kb: KnowledgeBase, ir: DslIr, options: CompileOptions = {}): CompileResult {
  const target = ir.targets[0];
  if (!target) throw new DslError('E101', '没有 target');
  if (ir.targets.length > 1) throw new DslError('E102', 'M1 只支持一个 target；多目标在 M2 支持');
  const item = kb.resolveItem(target.item);
  if (!item) throw new DslError('E103', `知识库里找不到物品：${target.item}`);
  const warnings: string[] = [];
  if (ir.raw.length) warnings.push('W501 外部原料清单暂未参与展开（M2 支持），当前按自动推断。');
  if (ir.modules.length || ir.connections.length) warnings.push('W502 自定义 module/connect 暂未参与布局（M2 支持），当前按 BOM 自动展开。');
  if (ir.groups.length || ir.stations.length || ir.constraints.length) warnings.push('W503 group/station/constraint 暂未参与摆放（M2 支持）。');
  if (ir.options.latitude !== undefined && ir.options.latitude !== 0) warnings.push('W504 纬度参数暂未参与间距计算（M2 支持），当前按赤道紧凑排。');
  if (ir.options.allowVerticalBelt !== undefined && ir.options.allowVerticalBelt) warnings.push('W505 垂直带开关暂未参与布局（M2 支持）。');

  const buildingTier = ir.options.assembler ? (TIER_BY_ASSEMBLER[ir.options.assembler] ?? options.buildingTier ?? 'base') : (options.buildingTier ?? 'base');
  const plan = kb.plan(item.id, { rate: target.rate, buildingTier });
  const built = planBlueprint(plan, {
    layout: 'factory',
    name: options.name ?? `DSL-${item.name}-${target.rate}`,
    beltType: options.beltType ?? 2,
    insertType: options.insertType ?? 3,
    gameVersion: options.gameVersion,
  });
  const meta = built.meta as CompileResult['meta'];
  return { text: built.text, meta, warnings: [...warnings, ...(meta.warnings ?? [])], plan };
}
