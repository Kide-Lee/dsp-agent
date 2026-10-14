/**
 * 极简结构化日志。
 *
 * 硬性约束（见 design.md §7.1.4 / M0 验收脚本）：
 *   **所有日志只走 stderr**，stdout 只留给那一行机器可读的 `READY {...}`。
 *   这样测试脚本可以用 `read -r line` 干净地抓取启动信息。
 *
 * 格式：每行一个 JSON 对象
 *   {"ts":"2025-01-01T00:00:00.000Z","level":"info","msg":"...", ...}
 */

export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

const LEVEL_WEIGHT: Record<LogLevel, number> = {
  debug: 10,
  info: 20,
  warn: 30,
  error: 40,
};

export type LogFields = Record<string, unknown>;

function isLevel(value: string): value is LogLevel {
  return value === 'debug' || value === 'info' || value === 'warn' || value === 'error';
}

function initialLevel(): LogLevel {
  const raw = (process.env['VIBE_DSP_LOG_LEVEL'] ?? '').toLowerCase();
  return isLevel(raw) ? raw : 'info';
}

let currentLevel: LogLevel = initialLevel();

/** 调整日志级别（CLI `--log-level` 用）。非法值会被忽略并返回 false。 */
export function setLevel(level: string): boolean {
  const normalized = level.toLowerCase();
  if (!isLevel(normalized)) return false;
  currentLevel = normalized;
  return true;
}

export function getLevel(): LogLevel {
  return currentLevel;
}

/**
 * 写一条日志。**任何情况下都不会抛异常**：
 * 日志本身永远不该成为新的故障源（例如字段里有循环引用时 JSON.stringify 会抛）。
 */
function write(level: LogLevel, msg: string, fields?: LogFields): void {
  if (LEVEL_WEIGHT[level] < LEVEL_WEIGHT[currentLevel]) return;

  const record: LogFields = {
    ts: new Date().toISOString(),
    level,
    msg,
  };
  if (fields) {
    for (const key of Object.keys(fields)) {
      const value = fields[key];
      if (value === undefined) continue;
      record[key] = value;
    }
  }

  let line: string;
  try {
    line = JSON.stringify(record);
  } catch {
    try {
      line = JSON.stringify({ ts: record['ts'], level, msg, logFieldsError: 'unserializable' });
    } catch {
      line = `{"level":"${level}","msg":"<unserializable log record>"}`;
    }
  }

  try {
    process.stderr.write(line + '\n');
  } catch {
    /* stderr 关了也不该崩 */
  }
}

export interface Logger {
  debug(msg: string, fields?: LogFields): void;
  info(msg: string, fields?: LogFields): void;
  warn(msg: string, fields?: LogFields): void;
  error(msg: string, fields?: LogFields): void;
  /** 派生一个始终带上这些字段的 logger（例如 sessionId）。 */
  child(bindings: LogFields): Logger;
}

/** 把 error 归一化成可安全序列化的字段。 */
export function errorFields(err: unknown): LogFields {
  if (err instanceof Error) {
    const out: LogFields = { errorName: err.name, errorMessage: err.message };
    if (err.stack) out['errorStack'] = err.stack;
    const code = (err as NodeJS.ErrnoException).code;
    if (code) out['errorCode'] = code;
    return out;
  }
  return { errorMessage: String(err) };
}

function makeLogger(bindings: LogFields): Logger {
  const merge = (fields?: LogFields): LogFields =>
    fields ? { ...bindings, ...fields } : bindings;
  return {
    debug: (msg, fields) => write('debug', msg, merge(fields)),
    info: (msg, fields) => write('info', msg, merge(fields)),
    warn: (msg, fields) => write('warn', msg, merge(fields)),
    error: (msg, fields) => write('error', msg, merge(fields)),
    child: (extra) => makeLogger({ ...bindings, ...extra }),
  };
}

export const log: Logger = makeLogger({});
