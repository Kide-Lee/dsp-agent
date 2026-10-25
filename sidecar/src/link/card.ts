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
  /** 本轮最终回答原文；用于区分重复提问（「现在什么情况」）。 */
  reply?: string;
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

/** 持久化记录的校验；planet/building/vein 三类都必须能回放，缺 id 的坏行跳过。 */
export function isStoredLinkCard(value: unknown): value is LinkCardPayload {
  const card = asRecord(value);
  if (!card) return false;
  if (card['kind'] !== 'planet' && card['kind'] !== 'building' && card['kind'] !== 'vein') return false;
  if (typeof card['planetId'] !== 'number' || card['planetId'] <= 0) return false;
  if (typeof card['name'] !== 'string' || card['name'].trim().length === 0) return false;
  if (card['kind'] === 'building' &&
      (typeof card['buildingId'] !== 'number' || card['buildingId'] <= 0)) return false;
  if (card['kind'] === 'vein' &&
      (typeof card['veinId'] !== 'number' || card['veinId'] <= 0)) return false;
  return true;
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

/** 回答原文匹配；历史可能被截断（末尾省略号），两种形态都要能对上。 */
function replyMatchesHistory(historyText: string, reply: string): boolean {
  if (!reply) return true;
  if (historyText === reply) return true;
  if (historyText.endsWith('…')) {
    const prefix = historyText.slice(0, -1);
    return prefix.length > 0 && reply.startsWith(prefix);
  }
  return false;
}

/**
 * 把记录挂到每一轮的最后一条 assistant。
 * 优先用「提问 + 回答原文」双匹配，避免重复提问时把卡片串到别的回合；
 * 老记录没有 reply 时退回只匹配提问。
 */
export function attachLinkCards(
  messages: HistoryMessage[],
  records: readonly LinkCardRecord[],
): HistoryMessage[] {
  if (messages.length === 0 || records.length === 0) return messages;

  const pending = records.filter(
    (record) => record.prompt.trim().length > 0 && record.card !== null,
  );
  if (pending.length === 0) return messages;

  const consumed = new Set<number>();
  for (let i = 0; i < messages.length; i++) {
    const message = messages[i];
    if (!message || message.role !== 'user') continue;

    let assistantIndex = -1;
    for (let j = i + 1; j < messages.length; j++) {
      const next = messages[j];
      if (!next) continue;
      if (next.role === 'user') break;
      if (next.role === 'assistant') assistantIndex = j;
    }
    if (assistantIndex < 0) continue;
    const assistant = messages[assistantIndex]!;

    const cards: LinkCardPayload[] = [];
    for (let r = 0; r < pending.length; r++) {
      if (consumed.has(r)) continue;
      const record = pending[r]!;
      if (!promptMatchesHistory(message.text, record.prompt.trim())) continue;
      if (record.reply !== undefined && !replyMatchesHistory(assistant.text, record.reply)) continue;
      consumed.add(r);
      cards.push(record.card);
    }
    if (cards.length === 0) continue;

    messages[assistantIndex] = {
      ...assistant,
      linkCards: [...(assistant.linkCards ?? []), ...cards],
    };
  }
  return messages;
}
