#!/usr/bin/env node
/**
 * dsp-agent sidecar CLI（M0）。
 *
 *   dsp-agent-sidecar serve [--port 8765] [--data-dir PATH] [--token TOKEN] [--host 127.0.0.1]
 *   dsp-agent-sidecar --selfcheck      # 发布流水线的空目录冒烟自检：打印版本号后退出
 *   dsp-agent-sidecar --help | --version
 *
 * 约定（M0 验收脚本依赖它）：
 *   - stdout **只**输出一行机器可读的启动信息：`READY {"port":8765,"token":"…"}`
 *   - 其余日志全部走 stderr（见 log.ts）
 *   - 启动后写 `<data-dir>/runtime.json`，退出时删除
 */

import { randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { isAbsolute, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { DEFAULT_PORT, PROTOCOL_VERSION, type AgentInfo } from './protocol';
import { errorFields, log, setLevel } from './log';
import { startServer, type RunningServer } from './server';

/**
 * 打包成单文件（esbuild --bundle，见 scripts/build-sidecar.sh）后 `__dirname` 里没有
 * package.json，所以版本号内嵌一份，package.json 只用来交叉核对（不一致时警告）。
 */
const SERVER_VERSION = '0.1.0';

/** M0 没有 LLM：agent 身份如实报告为 echo。M1 会由 pi runtime 覆盖这两个字段。 */
const AGENT_INFO: AgentInfo = { provider: 'echo', model: 'm0-echo', available: true };

/** M0 不注册任何工具（协议 §4.2 的 tools 为空数组）。 */
const REGISTERED_TOOLS: readonly string[] = [];

const DEFAULT_HOST = '127.0.0.1';
const DEFAULT_DATA_DIR_NAME = '.dsp-agent-dev';

interface ServeArgs {
  host: string;
  port: number;
  dataDir: string;
  token: string;
}

function usage(): string {
  return [
    'dsp-agent sidecar (M0)',
    '',
    '用法:',
    '  dsp-agent-sidecar serve [选项]',
    '',
    '选项:',
    `  --port <n>        监听端口（默认 ${DEFAULT_PORT}，0 = 随机端口）`,
    `  --host <addr>     监听地址（默认 ${DEFAULT_HOST}）`,
    `  --data-dir <path> 数据目录（默认 ~/${DEFAULT_DATA_DIR_NAME}）；运行时写 runtime.json`,
    '  --token <token>   握手 token（默认随机 32 字节 hex）',
    '  --log-level <l>   debug | info | warn | error（默认 info，也可用 DSP_AGENT_LOG_LEVEL）',
    '  -h, --help        显示本帮助',
    '  -v, --version     显示版本号',
    '      --selfcheck   打印版本号并退出（发布流水线冒烟自检）',
    '',
    '环境变量:',
    '  DSP_AGENT_LOG_LEVEL   同 --log-level',
  ].join('\n');
}

function readPackageVersion(): string | null {
  try {
    const pkgPath = join(__dirname, '..', 'package.json');
    if (!existsSync(pkgPath)) return null;
    const parsed: unknown = JSON.parse(readFileSync(pkgPath, 'utf8'));
    if (parsed && typeof parsed === 'object' && typeof (parsed as { version?: unknown }).version === 'string') {
      return (parsed as { version: string }).version;
    }
  } catch {
    /* 打包后读不到 package.json 是正常情况 */
  }
  return null;
}

function expandHome(input: string): string {
  if (input === '~') return homedir();
  if (input.startsWith('~/') || input.startsWith('~\\')) return join(homedir(), input.slice(2));
  return isAbsolute(input) ? input : resolve(input);
}

function parsePort(raw: string | undefined): number {
  if (raw === undefined) return DEFAULT_PORT;
  const value = Number(raw);
  if (!Number.isInteger(value) || value < 0 || value > 65535) {
    throw new Error(`--port 必须是 0..65535 的整数，收到 "${raw}"`);
  }
  return value;
}

export function newToken(): string {
  return randomBytes(32).toString('hex');
}

interface RuntimeFile {
  port: number;
  host: string;
  token: string;
  pid: number;
  startedAt: string;
  version: string;
}

function runtimePathFor(dataDir: string): string {
  return join(dataDir, 'runtime.json');
}

function writeRuntimeFile(dataDir: string, runtime: RuntimeFile): string {
  mkdirSync(dataDir, { recursive: true });
  const target = runtimePathFor(dataDir);
  // 仅本机用户可读（协议 §1：token 只在本机可读的 runtime.json 里）。
  writeFileSync(target, `${JSON.stringify(runtime, null, 2)}\n`, { encoding: 'utf8', mode: 0o600 });
  return target;
}

/** 只删自己的那份 runtime.json（避免踩掉另一个实例的状态）。 */
function removeRuntimeFile(dataDir: string, pid: number): void {
  const target = runtimePathFor(dataDir);
  try {
    if (!existsSync(target)) return;
    let owned = true;
    try {
      const parsed: unknown = JSON.parse(readFileSync(target, 'utf8'));
      if (parsed && typeof parsed === 'object') {
        const filePid = (parsed as { pid?: unknown }).pid;
        if (typeof filePid === 'number' && filePid !== pid) owned = false;
      }
    } catch {
      owned = true; // 内容坏了，直接清掉
    }
    if (!owned) {
      log.warn('runtime.json 属于其它进程，保留不动', { path: target });
      return;
    }
    rmSync(target, { force: true });
    log.info('已删除 runtime.json', { path: target });
  } catch (err) {
    log.warn('删除 runtime.json 失败', { path: target, ...errorFields(err) });
  }
}

async function runServe(args: ServeArgs): Promise<void> {
  const runtime: RuntimeFile = {
    port: args.port,
    host: args.host,
    token: args.token,
    pid: process.pid,
    startedAt: new Date().toISOString(),
    version: SERVER_VERSION,
  };

  let server: RunningServer;
  try {
    server = await startServer({
      host: args.host,
      port: args.port,
      token: args.token,
      version: SERVER_VERSION,
      agent: AGENT_INFO,
      tools: REGISTERED_TOOLS,
    });
  } catch (err) {
    const code = (err as NodeJS.ErrnoException).code;
    if (code === 'EADDRINUSE') {
      log.error(`${args.host}:${args.port} 已被占用；请换端口或先停掉旧的 sidecar`, errorFields(err));
    } else if (code === 'EACCES') {
      log.error(`没有权限监听 ${args.host}:${args.port}`, errorFields(err));
    } else {
      log.error('启动失败', errorFields(err));
    }
    process.exitCode = 1;
    return;
  }

  // runtime.json 用实际端口（--port 0 时由内核分配）。
  runtime.port = server.port;
  let runtimeFile: string;
  try {
    runtimeFile = writeRuntimeFile(args.dataDir, runtime);
  } catch (err) {
    log.error('写入 runtime.json 失败', { dataDir: args.dataDir, ...errorFields(err) });
    await server.close();
    process.exitCode = 1;
    return;
  }

  log.info('sidecar 已就绪', {
    url: server.url,
    runtimeFile,
    version: SERVER_VERSION,
    protocol: PROTOCOL_VERSION,
    agent: `${AGENT_INFO.provider}/${AGENT_INFO.model}`,
    pid: process.pid,
  });

  // stdout 只留给这一行，测试脚本靠它抓端口与 token。
  process.stdout.write(`READY ${JSON.stringify({ port: server.port, token: args.token })}\n`);

  let shuttingDown = false;
  const shutdown = async (reason: string, exitCode = 0): Promise<void> => {
    if (shuttingDown) return;
    shuttingDown = true;
    log.info('开始退出', { reason });
    try {
      await server.close();
    } catch (err) {
      log.error('关闭服务时出错', errorFields(err));
    }
    removeRuntimeFile(args.dataDir, runtime.pid);
    process.exit(exitCode);
  };

  process.on('SIGINT', () => void shutdown('SIGINT'));
  process.on('SIGTERM', () => void shutdown('SIGTERM'));
  process.on('SIGHUP', () => void shutdown('SIGHUP'));

  // 兜底：任何退出路径都要清掉 runtime.json。
  process.on('exit', () => removeRuntimeFile(args.dataDir, runtime.pid));

  process.on('uncaughtException', (err) => {
    log.error('未捕获异常', errorFields(err));
    void shutdown('uncaughtException', 1);
  });
  process.on('unhandledRejection', (reason) => {
    log.error('未处理的 Promise 拒绝', errorFields(reason));
    void shutdown('unhandledRejection', 1);
  });
}

interface CliValues {
  port?: string;
  host?: string;
  'data-dir'?: string;
  token?: string;
  'log-level'?: string;
  selfcheck?: boolean;
  help?: boolean;
  version?: boolean;
}

async function main(argv: string[]): Promise<void> {
  let values: CliValues;
  let positionals: string[];

  try {
    const parsed = parseArgs({
      args: argv,
      allowPositionals: true,
      strict: true,
      options: {
        port: { type: 'string' },
        host: { type: 'string' },
        'data-dir': { type: 'string' },
        token: { type: 'string' },
        'log-level': { type: 'string' },
        selfcheck: { type: 'boolean' },
        help: { type: 'boolean', short: 'h' },
        version: { type: 'boolean', short: 'v' },
      },
    });
    values = parsed.values as CliValues;
    positionals = [...parsed.positionals];
  } catch (err) {
    process.stderr.write(`${(err as Error).message}\n\n${usage()}\n`);
    process.exit(2);
    return;
  }

  const pkgVersion = readPackageVersion();

  if (values['log-level'] !== undefined && !setLevel(values['log-level'])) {
    process.stderr.write(`非法 --log-level "${values['log-level']}"（可选：debug|info|warn|error）\n`);
    process.exit(2);
    return;
  }

  if (values['help']) {
    process.stdout.write(`${usage()}\n`);
    return;
  }

  if (values['version']) {
    process.stdout.write(`${SERVER_VERSION}\n`);
    return;
  }

  if (values['selfcheck']) {
    // scripts/build-sidecar.sh 在空目录里跑这一条：不碰网络、不写文件，只证明 bundle 能启动。
    process.stdout.write(`dsp-agent-sidecar ${SERVER_VERSION} (protocol ${PROTOCOL_VERSION})\n`);
    if (pkgVersion && pkgVersion !== SERVER_VERSION) {
      log.warn('内嵌版本号与 package.json 不一致', { embedded: SERVER_VERSION, package: pkgVersion });
    }
    return;
  }

  const command = positionals[0];
  if (command === undefined) {
    process.stderr.write(`缺少子命令\n\n${usage()}\n`);
    process.exit(2);
    return;
  }
  if (command !== 'serve') {
    process.stderr.write(`未知子命令 "${command}"\n\n${usage()}\n`);
    process.exit(2);
    return;
  }
  if (positionals.length > 1) {
    process.stderr.write(`serve 不接受额外位置参数：${positionals.slice(1).join(' ')}\n`);
    process.exit(2);
    return;
  }

  let port: number;
  try {
    port = parsePort(values['port']);
  } catch (err) {
    process.stderr.write(`${(err as Error).message}\n`);
    process.exit(2);
    return;
  }

  const dataDir = expandHome(values['data-dir'] ?? join(homedir(), DEFAULT_DATA_DIR_NAME));
  const host = values['host'] ?? DEFAULT_HOST;
  const token = values['token'] ?? newToken();

  if (token.length === 0) {
    process.stderr.write('--token 不能为空\n');
    process.exit(2);
    return;
  }
  if (host.trim().length === 0) {
    process.stderr.write('--host 不能为空\n');
    process.exit(2);
    return;
  }
  if (values['data-dir'] !== undefined && values['data-dir'].trim().length === 0) {
    process.stderr.write('--data-dir 不能为空\n');
    process.exit(2);
    return;
  }

  if (pkgVersion && pkgVersion !== SERVER_VERSION) {
    log.warn('内嵌版本号与 package.json 不一致', { embedded: SERVER_VERSION, package: pkgVersion });
  }

  log.info('启动中', {
    version: SERVER_VERSION,
    protocol: PROTOCOL_VERSION,
    host,
    port,
    dataDir,
    tokenLength: token.length,
    tokenGenerated: values['token'] === undefined,
  });

  await runServe({ host, port, dataDir, token });
}

void main(process.argv.slice(2)).catch((err: unknown) => {
  log.error('致命错误', errorFields(err));
  process.exit(1);
});
