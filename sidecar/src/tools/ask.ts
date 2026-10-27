import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import type { JsonObject } from '../protocol.js';
import type { GameToolHost } from './host.js';

/** 交互问答工具名；mod 在进入游戏工具分发前拦截。 */
export const UI_ASK_TOOL_NAME = 'ask_user';
export const UI_ASK_TOOL_NAME_ALIASES = ['ui_ask_user'] as const;

/** mod 声明支持游戏内结构化提问。 */
export const CAPABILITY_UI_ASK = 'ui.ask.v1';

/** 玩家阅读/输入可能需要几分钟；其它工具仍然最多 30s。 */
export const UI_ASK_TIMEOUT_MS = 600000;

interface AskAnswer {
  index?: number;
  question?: string;
  answer?: string | null;
  optionIndex?: number;
  answers?: string[];
  optionIndexes?: number[];
  custom?: boolean;
  skipped?: boolean;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function formatAnswers(data: unknown): string {
  if (!isObject(data) || !Array.isArray(data['answers'])) {
    try {
      return JSON.stringify(data);
    } catch {
      return String(data);
    }
  }

  const answers = data['answers'] as AskAnswer[];
  if (answers.length === 0) return '玩家没有作答。';

  const lines = ['玩家已回答：'];
  for (let i = 0; i < answers.length; i += 1) {
    const item = answers[i] ?? {};
    const question = typeof item.question === 'string' ? item.question : `问题 ${i + 1}`;
    if (item.skipped === true) {
      lines.push(`${i + 1}. ${question} → （跳过）`);
      continue;
    }
    const values = Array.isArray(item.answers) && item.answers.length > 0
      ? item.answers.filter((value): value is string => typeof value === 'string')
      : (typeof item.answer === 'string' ? [item.answer] : []);
    const suffix = item.custom === true ? '（含自定义）' : '';
    lines.push(`${i + 1}. ${question} → ${values.join('、') || '（空）'}${suffix}`);
  }
  return lines.join('\n');
}

/**
 * `ask_user`：把结构化问题发给当前绑定的 mod 连接，等玩家在游戏窗口里回答。
 *
 * 命名与参数形状对齐 Pi 社区包 `pi-ask-user`（`ask_user`），便于复用社区提示词；
 * 但本工具由 sidecar 直接桥接到游戏 UGUI，不加载 pi 的 TUI 扩展。
 *
 * 复用 tool_call / tool_result：
 *  - 不新增协议帧；
 *  - 自动继承连接路由、断线取消与超时；
 *  - mod 负责把 `choose` 面板替换到输入栏。
 */
export function createAskTool(host: GameToolHost): ToolDefinition {
  return {
    name: UI_ASK_TOOL_NAME,
    label: '向玩家提问',
    description:
      '向玩家展示一个或多个带选项的问题，等待玩家在游戏窗口里选择或输入答案。' +
      '当需要玩家明确偏好、取舍或确认，而现有信息不足以判断时使用。' +
      '一次最多 8 题，每题最多 4 个选项；选项尽量具体，玩家也可以自定义输入。需要多个答案时把 allowMultiple 设为 true。',
    promptGuidelines: [
      '需要玩家明确选择/偏好/确认，且无法从游戏状态或知识库推断时使用。',
      '不要问能自己查到或验证的事实；能给出 2–4 个具体选项时优先给选项。',
      '一次可以问多个相关问题，UI 会逐题展示并允许玩家返回上一题；提交按钮在非最后一题显示“下一个”。',
      '玩家可能跳过问题；收到 skipped=true 时不要重复追问同一题。',
    ],
    parameters: Type.Object({
      // 与 pi-ask-user 的单题/多题形状兼容。options 使用扁平对象，
      // 不用 anyOf：部分 provider 代理会丢弃或拒绝 anyOf。
      question: Type.Optional(Type.String({ description: '单个问题；与 questions 二选一' })),
      questions: Type.Optional(
        Type.Array(
          Type.Object({
            question: Type.String({ description: '要问玩家的完整问题' }),
            context: Type.Optional(Type.String({ description: '可选上下文' })),
            header: Type.Optional(Type.String({ description: '可选短标题' })),
            options: Type.Optional(
              Type.Array(
                Type.Object({
                  title: Type.String({ description: '选项文字' }),
                  description: Type.Optional(Type.String({ description: '选项补充说明' })),
                }),
                { description: '0–4 个选项', maxItems: 4 },
              ),
            ),
            allowMultiple: Type.Optional(
              Type.Boolean({ description: '是否多选；多选时每个选项前显示复选框，默认 false' }),
            ),
            allowFreeform: Type.Optional(
              Type.Boolean({ description: '是否允许玩家输入自定义答案，默认 true' }),
            ),
          }),
          { description: '1–8 个问题；UI 逐题展示', minItems: 1, maxItems: 8 },
        ),
      ),
      context: Type.Optional(Type.String({ description: '可选上下文' })),
      options: Type.Optional(
        Type.Array(
          Type.Object({
            title: Type.String({ description: '选项文字' }),
            description: Type.Optional(Type.String({ description: '选项补充说明' })),
          }),
          { description: '单题选项，0–4 个', maxItems: 4 },
        ),
      ),
      allowMultiple: Type.Optional(Type.Boolean({ description: '是否多选，默认 false' })),
      allowFreeform: Type.Optional(Type.Boolean({ description: '是否允许自定义输入，默认 true' })),
    }),
    executionMode: 'sequential',
    async execute(_toolCallId, params, signal) {
      const result = await host.call(UI_ASK_TOOL_NAME, (params ?? {}) as JsonObject, {
        timeoutMs: UI_ASK_TIMEOUT_MS,
        signal,
      });

      if (!result.ok) {
        const code = result.error?.code ?? 'tool_failed';
        const message = result.error?.message ?? '未知错误';
        return {
          content: [{ type: 'text' as const, text: `[${code}] ${message}` }],
          details: result,
        };
      }

      return {
        content: [{ type: 'text' as const, text: formatAnswers(result.data) }],
        details: result,
      };
    },
  };
}
