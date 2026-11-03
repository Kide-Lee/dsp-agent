// blueprint_compile：执行 JS 子集 DSL -> IR -> 编译 -> 保存蓝图。
import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import type { KnowledgeBase } from '../../kb/knowledge.js';
import type { BlueprintWorkspace } from '../workspace.js';
import { runDsl } from './sandbox.js';
import { compileIr } from './compile.js';
import { DslError, type DslIr } from './ir.js';

type ToolReply = { content: { type: 'text'; text: string }[]; details: unknown };

function reply(payload: unknown, ok: boolean, code?: string): ToolReply {
  return {
    content: [{ type: 'text', text: ok ? JSON.stringify(payload, null, 2) : `[${code ?? 'blueprint_dsl_error'}] ${JSON.stringify(payload, null, 2)}` }],
    details: payload,
  };
}

function safeName(name: string): string {
  return name.replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_').slice(0, 80) || 'DSL-蓝图';
}

export function createDslTool(kb: KnowledgeBase, ws: BlueprintWorkspace): ToolDefinition {
  return {
    name: 'blueprint_compile',
    label: '编译蓝图 DSL',
    description:
      '编译一段蓝图 DSL（JavaScript 子集）并保存为蓝图文件。' +
      'DSL 在 QuickJS 沙箱里执行，只能调用 target/raw/options/module/connect/group/station/constraint。' +
      '编译是确定性的：展开配方、算机器数、站连站布局、校验，然后出图。',
    promptGuidelines: [
      '玩家要求用代码描述产线、或要可检查、可反复修改的蓝图时使用。',
      '先写最小源码：target(物品, 每分钟) + raw(...)，需要时再加 options。',
      '编译失败会返回带错误码的诊断；按建议改源码后重试，不要改用 blueprint_generate 绕过。',
    ],
    parameters: Type.Object({
      source: Type.String({ description: 'DSL 源码（JavaScript 子集）' }),
      dir: Type.Optional(Type.String({ description: '保存目录（相对蓝图根，默认 /IcarusII）' })),
      name: Type.Optional(Type.String({ description: '蓝图文件名（不含扩展名）' })),
      beltType: Type.Optional(Type.Integer({ minimum: 0, maximum: 2, description: '传送带档位：0 黄 / 1 绿 / 2 蓝（默认 2）' })),
      insertType: Type.Optional(Type.Integer({ minimum: 0, maximum: 3, description: '分拣器档位：0..3（默认 3 集装）' })),
      buildingTier: Type.Optional(
        Type.Union([Type.Literal('base'), Type.Literal('mk2'), Type.Literal('mk3')], { description: '机器档位（默认 base）' }),
      ),
    }),
    async execute(_callId: string, args: Record<string, unknown>) {
      const source = typeof args['source'] === 'string' ? args['source'] : '';
      if (!source.trim()) return reply({ error: 'source 不能为空' }, false, 'bad_request');
      let ir: DslIr;
      try {
        ir = await runDsl(source);
      } catch (error) {
        const failure = error instanceof DslError ? { code: error.code, message: error.message } : { code: 'E000', message: error instanceof Error ? error.message : String(error) };
        return reply({ error: failure.message, code: failure.code }, false, 'blueprint_dsl_error');
      }
      let compiled;
      try {
        compiled = compileIr(kb, ir, {
          name: typeof args['name'] === 'string' ? args['name'] : undefined,
          beltType: typeof args['beltType'] === 'number' ? args['beltType'] : undefined,
          insertType: typeof args['insertType'] === 'number' ? args['insertType'] : undefined,
          buildingTier: args['buildingTier'] === 'mk2' || args['buildingTier'] === 'mk3' ? args['buildingTier'] : 'base',
          gameVersion: kb.gameVersion,
        });
      } catch (error) {
        if (error instanceof DslError) return reply({ error: error.message, code: error.code }, false, 'blueprint_dsl_error');
        return reply({ error: error instanceof Error ? error.message : String(error), code: 'E200' }, false, 'blueprint_dsl_error');
      }
      let targetDir;
      try {
        targetDir = ws.resolveTargetDir(typeof args['dir'] === 'string' ? args['dir'] : undefined);
      } catch (error) {
        return reply({ error: error instanceof Error ? error.message : String(error) }, false, 'blueprint_save_failed');
      }
      const unique = ws.uniqueFileName(targetDir, safeName(typeof args['name'] === 'string' && args['name'].trim() ? args['name'].trim() : `DSL-${ir.targets[0]?.item ?? '蓝图'}-${ir.targets[0]?.rate ?? 0}`));
      try {
        ws.write(unique.virtual.virtual, compiled.text);
      } catch (error) {
        return reply({ error: error instanceof Error ? error.message : String(error) }, false, 'blueprint_save_failed');
      }
      const meta = compiled.meta;
      return reply(
        {
          saved: true,
          fileName: unique.name,
          path: unique.virtual.real,
          width: meta.width ?? 0,
          height: meta.height ?? 0,
          buildings: meta.buildings ?? 0,
          machines: meta.machines ?? 0,
          warnings: compiled.warnings,
          report: meta.report ?? null,
          targets: ir.targets,
          raw: ir.raw,
        },
        true,
      );
    },
  };
}
