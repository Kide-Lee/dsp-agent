// 蓝图 DSL 的中间表示：JS 源码在沙箱里执行后得到这份纯 JSON，编译器只认它。
export interface DslTarget {
  item: string;
  rate: number;
}

export interface DslOptions {
  belt?: string;
  sorter?: string;
  assembler?: string;
  smelter?: string;
  chemical?: string;
  lab?: string;
  proliferator?: number;
  maxWidth?: number;
  maxHeight?: number;
  aspect?: number;
  latitude?: number;
  elevation?: number;
  allowVerticalBelt?: boolean;
  recipePolicy?: string;
  moduleSpacing?: string;
}

export interface DslModule {
  id: string;
  item: string;
  machines: 'auto' | number;
  building: string | null;
}

export interface DslConnection {
  from: string;
  to: string;
  rate?: number;
}

export interface DslIr {
  targets: DslTarget[];
  raw: string[];
  options: DslOptions;
  modules: DslModule[];
  connections: DslConnection[];
  groups: { name: string; members: string[]; options: Record<string, unknown> }[];
  stations: { name: string; options: Record<string, unknown> }[];
  constraints: string[];
}

export class DslError extends Error {
  constructor(public code: string, message: string) {
    super(message);
    this.name = 'DslError';
  }
}

/** 单张蓝图的目标产量上限：超过这个量级应先拆产线，不能把 sidecar 拖死。 */
export const MAX_TARGET_RATE = 1_000_000;

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value);

/** 校验沙箱返回的原始对象；形状不对就报 E0xx，由调用方转成诊断。 */
export function normalizeIr(value: unknown): DslIr {
  if (!isObject(value)) throw new DslError('E001', 'DSL 没有返回结果对象');
  const targets: DslTarget[] = [];
  for (const row of Array.isArray(value.target) ? value.target : []) {
    if (!isObject(row) || typeof row.item !== 'string') throw new DslError('E002', 'target(item, rate) 参数不合法');
    const rate = Number(row.rate);
    if (!Number.isFinite(rate) || !(rate > 0)) throw new DslError('E003', `target(${row.item}) 的产量必须是有限正数`);
    if (rate > MAX_TARGET_RATE) {
      throw new DslError('E011', `target(${row.item}) 的产量 ${rate} 超过单张蓝图上限 ${MAX_TARGET_RATE}`);
    }
    targets.push({ item: row.item, rate });
  }
  if (targets.length === 0) throw new DslError('E004', '没有 target：至少要写一个 target(物品, 每分钟)');
  const raw = (Array.isArray(value.raw) ? value.raw : []).map((item) => String(item));
  const options = isObject(value.options) ? (value.options as DslOptions) : {};
  const modules: DslModule[] = (Array.isArray(value.module) ? value.module : []).map((row, index) => {
    if (!isObject(row) || typeof row.item !== 'string') throw new DslError('E005', 'module(item, options) 参数不合法');
    const machines = row.machines === 'auto' ? 'auto' : Number(row.machines);
    if (machines !== 'auto' && !(machines > 0)) throw new DslError('E006', `module(${row.item}) 的 machines 必须是 auto 或正数`);
    return { id: String(row.id ?? `m${index + 1}`), item: row.item, machines, building: typeof row.building === 'string' ? row.building : null };
  });
  const connections: DslConnection[] = (Array.isArray(value.connect) ? value.connect : []).map((row) => {
    if (!isObject(row) || typeof row.from !== 'string' || typeof row.to !== 'string') throw new DslError('E007', 'connect(from, to, rate?) 参数不合法');
    return { from: row.from, to: row.to, ...(row.rate !== undefined ? { rate: Number(row.rate) } : {}) };
  });
  const groups = (Array.isArray(value.group) ? value.group : []).map((row) => ({
    name: String((row as { name?: unknown }).name ?? ''),
    members: Array.isArray((row as { members?: unknown }).members) ? ((row as { members: unknown[] }).members).map(String) : [],
    options: isObject((row as { options?: unknown }).options) ? ((row as { options: Record<string, unknown> }).options) : {},
  }));
  const stations = (Array.isArray(value.station) ? value.station : []).map((row) => ({
    name: String((row as { name?: unknown }).name ?? ''),
    options: isObject((row as { options?: unknown }).options) ? ((row as { options: Record<string, unknown> }).options) : {},
  }));
  const constraints = (Array.isArray(value.constraint) ? value.constraint : []).map(String);
  return { targets, raw, options, modules, connections, groups, stations, constraints };
}
