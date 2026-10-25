/**
 * 实体链接卡片的提取与历史挂接。
 *
 * 卡片在 mod 侧由 `game_tether_add` 自动生成；sidecar 这里只负责：
 *  - 从工具结果提取稳定目标字段，供持久化/历史回放；
 *  - 按「用户提问原文」把卡片挂回最终 assistant 消息。
 */

import type { HistoryMessage, LinkCardPayload } from '../protocol.js';
import { promptMatchesHistory } from '../blueprint/card.js';

export interface LinkCardRecord {
  prompt: string;
  timestamp?: number;
  card: LinkCardPayload;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) return null;
  return value as Record<string, unknown>;
}

function str(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : undefined;
}

function num(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

/** 从 `game_tether_add` 的 ToolResult 提取卡片；失败/非该工具返回 null。 */
export function extractLinkCard(
  toolName: string,
  result: unknown,
  isError = false,
): LinkCardPayload | null {
  if (isError || toolName !== 'game_tether_add') return null;
  const envelope = asRecord(result);
  if (!envelope) return null;
  const details = asRecord(envelope['details']) ?? envelope;
  const nested = asRecord(details['data']);
  const data = typeof details['ok'] === 'boolean' && nested ? nested : details;

  const kind = str(data['kind']);
  if (kind !== 'planet' && kind !== 'building' && kind !== 'vein') return null;
  const planetId = num(data['planetId']);
  const buildingId = num(data['buildingId']);
  const name = str(data['name']);
  if (!planetId || planetId <= 0 || !name) return null;
  if (kind === 'building' && (!buildingId || buildingId <= 0)) return null;
  const veinId = num(data['veinId']);
  if (kind === 'vein' && (!veinId || veinId <= 0)) return null;

  const card: LinkCardPayload = { kind, planetId, name };
  if (kind === 'building' && buildingId) card.buildingId = buildingId;
  if (kind === 'vein' && veinId) card.veinId = veinId;
  const subtitle = str(data['subtitle']);
  if (subtitle) card.subtitle = subtitle;
  const description = str(data['description']);
  if (description) card.description = description;
  const iconLabel = str(data['iconLabel']);
  if (iconLabel) card.iconLabel = iconLabel;
  return card;
}

export function compactLinkCard(card: LinkCardPayload): LinkCardPayload {
  const clip = (value: string | undefined, max: number): string | undefined => {
    if (value === undefined) return undefined;
    return value.length > max ? `${value.slice(0, max)}…` : value;
  };
  return {
    ...card,
    name: clip(card.name, 120) ?? card.name,
    subtitle: clip(card.subtitle, 120),
    description: clip(card.description, 1200),
    iconLabel: clip(card.iconLabel, 80),
  };
}

/** 把记录挂到每一轮的最后一条 assistant；同一 prompt 的多张卡按记录顺序。 */
export function attachLinkCards(
  messages: HistoryMessage[],
  records: readonly LinkCardRecord[],
): HistoryMessage[] {
  if (messages.length === 0 || records.length === 0) return messages;

  const groups = new Map<string, LinkCardPayload[]>();
  for (const record of records) {
    const prompt = record.prompt.trim();
    if (!prompt || !record.card) continue;
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
      linkCards: [...(target.linkCards ?? []), ...cards],
    };
  }
  return messages;
}
