/**
 * 会话历史的删除（M4e）。
 *
 * 聊天窗的「删除」必须落到 sidecar 的会话文件上：`save.bound.history` 是从
 * `.jsonl` 重放的，只删 UI 的话重进存档又会出现。这里做**纯函数**改写，
 * 方便离线测试；文件读写与内存会话重建在 Agent 里。
 *
 * 会话是树（`id` / `parentId`），但本项目实际只产生线性会话。
 * 删除后把保留条目按原顺序重新串成一条链；一旦遇到压缩/分支/上下文编辑条目
 * 就拒绝，避免把上下文树改坏。要撤销压缩请用 `removeCompactionFromEntries()`。
 */

export interface DeleteTurnOutcome {
  ok: boolean;
  removed: number;
  error?: string;
}

type Entry = Record<string, unknown>;

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function textOfContent(content: unknown): string {
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  const parts: string[] = [];
  for (const block of content) {
    if (!isRecord(block)) continue;
    if (block['type'] === 'text' && typeof block['text'] === 'string') parts.push(block['text']);
  }
  return parts.join('\n');
}

function userTextOf(entry: Entry): string | null {
  if (entry['type'] !== 'message') return null;
  const message = entry['message'];
  if (!isRecord(message) || message['role'] !== 'user') return null;
  return textOfContent(message['content']).trim();
}

function timestampOf(entry: Entry): number {
  const message = entry['message'];
  if (isRecord(message) && typeof message['timestamp'] === 'number') return message['timestamp'];
  const raw = entry['timestamp'];
  if (typeof raw === 'string') {
    const parsed = Date.parse(raw);
    if (Number.isFinite(parsed)) return parsed;
  }
  if (isRecord(message) && typeof message['timestamp'] === 'number') return message['timestamp'];
  return 0;
}

function isUnsupported(entry: Entry): boolean {
  const type = entry['type'];
  // pi 1.4 新增 context_edit：它用 targetId 指向一条旧消息，只改模型上下文。
  // 删除回合会让 targetId 悬空，所以和压缩/分支摘要一样拒绝，避免把会话树改坏。
  return type === 'compaction' || type === 'branch_summary' || type === 'context_edit';
}

/**
 * 从解析后的 JSONL 条目里删除一个问答回合。
 *
 * - `entries[0]` 必须是会话头；否则视为不支持的会话。
 * - 定位用户消息：优先时间戳（±1s），否则取最后一条同文本的用户消息。
 * - 删除范围：该用户消息到**下一条用户消息**之前（含助手/工具/模型切换等）。
 * - 保留条目按顺序重连 `parentId`。
 */
export function removeTurnFromEntries(
  entries: readonly unknown[],
  text: string,
  timestamp?: number,
): { outcome: DeleteTurnOutcome; entries?: unknown[] } {
  const wanted = text.trim();
  if (!wanted) return { outcome: { ok: false, removed: 0, error: 'turn_not_found' } };
  if (entries.length === 0) return { outcome: { ok: false, removed: 0, error: 'turn_not_found' } };

  const header = entries[0];
  if (!isRecord(header) || header['type'] !== 'session') {
    return { outcome: { ok: false, removed: 0, error: 'unsupported_session' } };
  }

  const body: Entry[] = [];
  for (const raw of entries.slice(1)) {
    if (!isRecord(raw)) return { outcome: { ok: false, removed: 0, error: 'unsupported_session' } };
    body.push(raw);
  }
  if (body.some(isUnsupported)) {
    return { outcome: { ok: false, removed: 0, error: 'unsupported_session' } };
  }

  let start = -1;
  if (typeof timestamp === 'number' && Number.isFinite(timestamp) && timestamp > 0) {
    let bestDiff = Number.POSITIVE_INFINITY;
    for (let i = 0; i < body.length; i += 1) {
      const entry = body[i] as Entry;
      if (userTextOf(entry) !== wanted) continue;
      const diff = Math.abs(timestampOf(entry) - timestamp);
      if (diff < bestDiff && diff <= 1000) {
        bestDiff = diff;
        start = i;
      }
    }
  }
  if (start < 0) {
    for (let i = body.length - 1; i >= 0; i -= 1) {
      if (userTextOf(body[i] as Entry) === wanted) {
        start = i;
        break;
      }
    }
  }
  if (start < 0) return { outcome: { ok: false, removed: 0, error: 'turn_not_found' } };

  let end = body.length;
  for (let i = start + 1; i < body.length; i += 1) {
    if (userTextOf(body[i] as Entry) !== null) {
      end = i;
      break;
    }
  }

  const removed = end - start;
  const kept: Entry[] = [];
  for (let i = 0; i < body.length; i += 1) {
    if (i >= start && i < end) continue;
    const entry = body[i] as Entry;
    if (typeof entry['id'] !== 'string' || entry['id'].length === 0) {
      return { outcome: { ok: false, removed: 0, error: 'unsupported_session' } };
    }
    kept.push(entry);
  }

  // 重新串链：第一条指向 null，其余指向前一条的 id。
  for (let i = 0; i < kept.length; i += 1) {
    const entry = kept[i] as Entry;
    entry['parentId'] = i === 0 ? null : (kept[i - 1] as Entry)['id'];
  }

  return { outcome: { ok: true, removed }, entries: [header, ...kept] };
}

export interface UndoCompactionOutcome {
  ok: boolean;
  removed: number;
  compactionId?: string;
  error?: string;
}

/**
 * 撤销最近一次（或指定的）上下文压缩：删掉 compaction entry，把它唯一的子条目
 * 接回 compaction 的 parent。旧消息重新成为模型可见上下文。
 *
 * 只允许撤销**最后一次**压缩（LIFO）：更早的压缩已经被后续压缩覆盖，
 * 单独删掉会破坏摘要链。多子节点（分支会话）拒绝改写。
 */
export function removeCompactionFromEntries(
  entries: readonly unknown[],
  compactionId?: string,
): { outcome: UndoCompactionOutcome; entries?: unknown[] } {
  if (entries.length === 0) {
    return { outcome: { ok: false, removed: 0, error: 'not_found' } };
  }
  const header = entries[0];
  if (!isRecord(header) || header['type'] !== 'session') {
    return { outcome: { ok: false, removed: 0, error: 'unsupported_session' } };
  }

  const body: Entry[] = [];
  for (const raw of entries.slice(1)) {
    if (!isRecord(raw)) {
      return { outcome: { ok: false, removed: 0, error: 'unsupported_session' } };
    }
    body.push(raw);
  }

  let latest = -1;
  for (let i = 0; i < body.length; i += 1) {
    if (body[i]?.['type'] === 'compaction') latest = i;
  }
  if (latest < 0) return { outcome: { ok: false, removed: 0, error: 'not_found' } };

  const target = body[latest] as Entry;
  const targetId = target['id'];
  if (typeof targetId !== 'string' || targetId.length === 0) {
    return { outcome: { ok: false, removed: 0, error: 'unsupported_session' } };
  }
  if (compactionId && compactionId !== targetId) {
    const requested = body.findIndex(
      (entry) => entry['type'] === 'compaction' && entry['id'] === compactionId,
    );
    // 指定的不是最近一次压缩：不允许跳过后续压缩单独撤销。
    return {
      outcome: {
        ok: false,
        removed: 0,
        compactionId: targetId,
        error: requested < 0 ? 'not_found' : 'not_latest',
      },
    };
  }

  const children: Entry[] = [];
  for (const entry of body) {
    if (entry['parentId'] === targetId) children.push(entry);
  }
  if (children.length > 1) {
    return { outcome: { ok: false, removed: 0, compactionId: targetId, error: 'unsupported_session' } };
  }
  if (latest < body.length - 1 && children.length === 0) {
    // 压缩后面还有条目，但链已经断了：不猜测，直接拒绝。
    return { outcome: { ok: false, removed: 0, compactionId: targetId, error: 'unsupported_session' } };
  }

  const parentId = target['parentId'];
  for (const child of children) {
    child['parentId'] = typeof parentId === 'string' || parentId === null ? parentId : null;
  }

  const next = body.filter((_, index) => index !== latest);
  return {
    outcome: { ok: true, removed: 1, compactionId: targetId },
    entries: [header, ...next],
  };
}

/** 把 JSONL 文本拆成条目；坏行直接判为不支持，绝不猜测。 */
export function parseSessionLines(raw: string): unknown[] | null {
  const out: unknown[] = [];
  for (const line of raw.split('\n')) {
    if (!line.trim()) continue;
    try {
      out.push(JSON.parse(line) as unknown);
    } catch {
      return null;
    }
  }
  return out;
}

export function serializeSessionEntries(entries: readonly unknown[]): string {
  return `${entries.map((entry) => JSON.stringify(entry)).join('\n')}\n`;
}
