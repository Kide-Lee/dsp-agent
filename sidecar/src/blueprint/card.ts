/**
 * 从蓝图工具结果里提取「蓝图卡片」载荷（协议附录 A.9）。
 *
 * 为什么单独一个模块：`agent.ts` 只负责在正确的时机（本轮收尾）把卡片发出去，
 * 而「哪个工具产出了哪张蓝图、结果里有哪些字段」是蓝图域自己的知识。
 * 这里不依赖 pi / WebSocket，测试可以直接断言。
 */

import { basename, join } from 'node:path';
import type { BlueprintCardPayload, BlueprintCardSource, HistoryMessage } from '../protocol.js';

/** 会产出蓝图文件的工具：sidecar 本地两个 + mod 侧两个。 */
export const BLUEPRINT_PRODUCING_TOOLS: readonly string[] = [
  'blueprint_generate',
  'blueprint_save',
  'game_copy_blueprint',
];

const PRODUCING = new Set(BLUEPRINT_PRODUCING_TOOLS);

function asRecord(value: unknown): Record<string, unknown> | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null;
  return value as Record<string, unknown>;
}

function str(value: unknown): string | undefined {
  if (typeof value !== 'string') return undefined;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : undefined;
}

function num(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function sourceFor(toolName: string, raw: unknown): BlueprintCardSource | undefined {
  const value = str(raw);
  if (value === 'generated' || value === 'downloaded' || value === 'copied') return value;
  if (toolName === 'blueprint_generate') return 'generated';
  if (toolName === 'blueprint_save') return 'downloaded';
  if (toolName === 'game_copy_blueprint') return 'copied';
  return undefined;
}

/**
 * `result` 是 pi 的 `AgentToolResult`（`{ content, details }`）：
 *   - sidecar 本地工具：`details` 就是工具自己的返回对象；
 *   - mod 工具：`details` 是 `ToolResultPayload`（`{ ok, data, ... }`）。
 *
 * 只认 `saved === true` 的结果；失败、超时、`saved:false` 一律不发卡片。
 */
export function extractBlueprintCard(
  toolName: string,
  result: unknown,
  isError = false,
): BlueprintCardPayload | null {
  if (isError || !PRODUCING.has(toolName)) return null;

  const envelope = asRecord(result);
  if (!envelope) return null;
  const details = asRecord(envelope['details']) ?? envelope;
  const nested = asRecord(details['data']);
  const data = typeof details['ok'] === 'boolean' && nested ? nested : details;
  if (data['saved'] !== true) return null;

  const rawPath = str(data['path']);
  const fileName = str(data['fileName']) ?? (rawPath ? basename(rawPath) : undefined);
  if (!fileName) return null;

  // 有些工具只给 dir + fileName；拼出 path 便于排障，mod 仍会优先用 fileName 回退。
  const dir = str(data['dir']);
  const path = rawPath ?? (dir ? join(dir, fileName) : undefined);

  const card: BlueprintCardPayload = { fileName };
  if (path !== undefined) card.path = path;

  const name = str(data['name']) ?? str(data['shortDesc']);
  if (name !== undefined) card.name = name;

  const item = str(data['item']);
  if (item !== undefined) card.item = item;

  const itemId = num(data['itemId']) ?? num(data['iconItemId']);
  if (itemId !== undefined) card.itemId = itemId;

  const rate = num(data['rate']);
  if (rate !== undefined) card.rate = rate;

  const buildings = num(data['buildings']);
  if (buildings !== undefined) card.buildings = buildings;

  const width = num(data['width']);
  if (width !== undefined) card.width = width;

  const height = num(data['height']);
  if (height !== undefined) card.height = height;

  const author = str(data['author']);
  if (author !== undefined) card.author = author;

  const version = str(data['version']);
  if (version !== undefined) card.version = version;

  const attributes = str(data['attributes']) ?? str(data['externalFields']);
  if (attributes !== undefined) card.attributes = attributes;

  const description = str(data['description']) ?? str(data['desc']);
  if (description !== undefined) card.description = description;

  const source = sourceFor(toolName, data['source']);
  if (source !== undefined) card.source = source;

  const license = str(data['license']);
  if (license !== undefined) card.license = license;

  const repository = str(data['repository']) ?? str(data['repo']);
  if (repository !== undefined) card.repository = repository;

  return card;
}

// ---------------------------------------------------------------- 历史回放（附录 A.6/A.9）

/** 侧车持久化的一条卡片记录：用它把卡片重新挂回对应回合。 */
export interface BlueprintCardRecord {
  /** 本轮用户提问原文（与 session 历史里的 user 消息对应）。 */
  prompt: string;
  /** 发出卡片的时间（毫秒），仅用于排障。 */
  timestamp?: number;
  card: BlueprintCardPayload;
}

/**
 * 把持久化的卡片挂回 `save.bound.history`：找到提问原文对应的 user 消息，
 * 再挂到它后面的第一条 assistant 消息上。找不到对应回合就跳过——
 * 宁可不回放，也不要把卡片挂到别的回答上。
 */
export function attachBlueprintCards(
  messages: HistoryMessage[],
  records: readonly BlueprintCardRecord[],
): HistoryMessage[] {
  if (messages.length === 0 || records.length === 0) return messages;

  // 按 prompt 分组：同一轮可能生成多张蓝图。
  const groups = new Map<string, BlueprintCardPayload[]>();
  for (const record of records) {
    const prompt = record.prompt.trim();
    if (!prompt || !record.card || typeof record.card.fileName !== 'string') continue;
    const list = groups.get(prompt) ?? [];
    list.push(record.card);
    groups.set(prompt, list);
  }
  if (groups.size === 0) return messages;

  const consumed = new Set<string>();
  for (let i = 0; i < messages.length; i++) {
    const message = messages[i];
    if (!message || message.role !== 'user') continue;

    let matched: string | null = null;
    for (const prompt of groups.keys()) {
      if (consumed.has(prompt)) continue;
      if (promptMatchesHistory(message.text, prompt)) {
        matched = prompt;
        break;
      }
    }
    if (matched === null) continue;

    // 找这一轮的**最后一条**回答：模型可能在工具调用前先写一段话，
    // 卡片必须挂在整轮收尾的 assistant 上，而不是中间过程。
    let assistantIndex = -1;
    for (let j = i + 1; j < messages.length; j++) {
      const next = messages[j];
      if (!next) continue;
      if (next.role === 'user') break;
      if (next.role === 'assistant') assistantIndex = j;
    }
    if (assistantIndex < 0) continue;

    consumed.add(matched);
    const cards = groups.get(matched) ?? [];
    if (cards.length === 0) continue;
    const target = messages[assistantIndex]!;
    messages[assistantIndex] = {
      ...target,
      cards: [...(target.cards ?? []), ...cards],
    };
  }
  return messages;
}

/** history 里的 user 文本可能被截断（带省略号）；两种形态都要能对上。 */
function promptMatchesHistory(historyText: string, prompt: string): boolean {
  if (historyText === prompt) return true;
  if (historyText.endsWith('…')) {
    const prefix = historyText.slice(0, -1);
    return prefix.length > 0 && prompt.startsWith(prefix);
  }
  return false;
}

/** 历史回放用的卡片副本：长文本截断，避免 40 条历史把帧撑爆。 */
export function compactBlueprintCard(card: BlueprintCardPayload): BlueprintCardPayload {
  const clip = (value: string | undefined, max: number): string | undefined => {
    if (value === undefined) return undefined;
    return value.length > max ? `${value.slice(0, max)}…` : value;
  };
  const out: BlueprintCardPayload = { fileName: clip(card.fileName, 200) ?? 'blueprint.txt' };
  const copy = (key: keyof BlueprintCardPayload, max: number): void => {
    const value = card[key];
    if (typeof value === 'string') {
      const clipped = clip(value, max);
      if (clipped !== undefined) (out as unknown as Record<string, unknown>)[key] = clipped;
    } else if (typeof value === 'number') {
      (out as unknown as Record<string, unknown>)[key] = value;
    }
  };
  copy('path', 400);
  copy('name', 160);
  copy('item', 80);
  copy('itemId', 0);
  copy('rate', 0);
  copy('buildings', 0);
  copy('width', 0);
  copy('height', 0);
  copy('author', 80);
  copy('version', 40);
  copy('attributes', 120);
  copy('description', 280);
  copy('source', 20);
  copy('license', 80);
  copy('repository', 240);
  return out;
}
