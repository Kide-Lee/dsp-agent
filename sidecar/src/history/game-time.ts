import type { HistoryMessage } from '../protocol.js';
import { promptMatchesHistory } from '../blueprint/card.js';

/** 回答完成时回存的游戏时间；优先按回答原文挂回，回退按提问原文。 */
export interface GameTimeRecord {
  prompt: string;
  reply?: string;
  gameTime: string;
  /** 写入时间（毫秒），仅用于排障。 */
  timestamp?: number;
}

/**
 * 把持久化的游戏时间挂回 `save.bound.history`。
 *
 * 先按 assistant 终稿原文匹配（能区分“继续”这类重复提问），
 * 再按 user 提问回退到整轮最后一条 assistant。找不到就跳过——
 * 宁可显示 `--:--`，也不要把时间挂到别的回答上。
 */
export function attachGameTimes(
  messages: HistoryMessage[],
  records: readonly GameTimeRecord[],
): HistoryMessage[] {
  if (messages.length === 0 || records.length === 0) return messages;

  // 同一提问（或同一提问+回答）的重复记录以最后一条为准。
  const promptOnly = new Map<string, { prompt: string; reply: string; gameTime: string }>();
  const withReply = new Map<string, { prompt: string; reply: string; gameTime: string }>();
  for (const record of records) {
    const prompt = record.prompt.trim();
    const reply = record.reply?.trim() ?? '';
    const gameTime = record.gameTime.trim();
    if (!prompt || !gameTime) continue;
    const item = { prompt, reply, gameTime };
    if (reply) withReply.set(`${prompt}\u0000${reply}`, item);
    else promptOnly.set(prompt, item);
  }
  const pending = [...promptOnly.values(), ...withReply.values()];
  if (pending.length === 0) return messages;

  const used = new Set<number>();

  // 1) 回答原文唯一匹配：重复的“继续”也能各归各的回合。
  for (let i = 0; i < messages.length; i++) {
    const message = messages[i];
    if (!message || message.role !== 'assistant' || !message.text) continue;

    // 最近的前一条 user 消息；有它时同时校验提问，避免相同回答串到别的回合。
    let promptText: string | null = null;
    let userIndex = -1;
    for (let j = i - 1; j >= 0; j--) {
      if (messages[j]?.role === 'user') {
        promptText = messages[j]!.text;
        userIndex = j;
        break;
      }
    }

    let found = -1;
    for (let r = 0; r < pending.length; r++) {
      if (used.has(r)) continue;
      const record = pending[r]!;
      if (!record.reply) continue;
      if (!replyMatchesHistory(message.text, record.reply)) continue;
      if (promptText && !promptMatchesHistory(promptText, record.prompt)) continue;
      found = r;
      break;
    }
    if (found < 0) continue;
    const record = pending[found]!;
    used.add(found);
    messages[i] = { ...message, gameTime: record.gameTime };
    // 用户气泡下面的 footer 也要显示时间；历史里只有回答完成时的游戏时间，
    // 拿它近似提问时间（同一回合内）。
    if (userIndex >= 0 && !messages[userIndex]!.gameTime) {
      messages[userIndex] = { ...messages[userIndex]!, gameTime: record.gameTime };
    }
  }

  // 2) 提问回退：只挂到该轮最后一条回答，且不覆盖已匹配的回答。
  for (let i = 0; i < messages.length; i++) {
    const message = messages[i];
    if (!message || message.role !== 'user') continue;

    let matched = -1;
    for (let r = 0; r < pending.length; r++) {
      if (used.has(r)) continue;
      if (promptMatchesHistory(message.text, pending[r]!.prompt)) {
        matched = r;
        break;
      }
    }
    if (matched < 0) continue;

    let assistantIndex = -1;
    for (let j = i + 1; j < messages.length; j++) {
      const next = messages[j];
      if (!next) continue;
      if (next.role === 'user') break;
      if (next.role === 'assistant') assistantIndex = j;
    }
    if (assistantIndex < 0) continue;

    const target = messages[assistantIndex]!;
    const gameTime = pending[matched]!.gameTime;
    used.add(matched);
    if (!message.gameTime) {
      messages[i] = { ...message, gameTime };
    }
    if (!target.gameTime) {
      messages[assistantIndex] = { ...target, gameTime };
    }
  }

  return messages;
}

/** history 的 assistant 文本可能被截断（带省略号）；两种形态都要能对上。 */
function replyMatchesHistory(historyText: string, reply: string): boolean {
  if (historyText === reply) return true;
  if (historyText.endsWith('…')) {
    const prefix = historyText.slice(0, -1);
    return prefix.length > 0 && reply.startsWith(prefix);
  }
  // 记录侧也可能被存储上限截断；历史文本更长时按前缀比较。
  return reply.length > historyText.length && reply.startsWith(historyText);
}
