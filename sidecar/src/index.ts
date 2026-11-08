#!/usr/bin/env node
/**
 * icarus-ii sidecar CLI（M1）。
 *
 *   icarus-ii-sidecar serve [--agent pi|echo] [--provider deepseek] [--model deepseek-v4-flash]
 *                           [--port 8765] [--data-dir PATH] [--token TOKEN] [--host 127.0.0.1]
 *   icarus-ii-sidecar --selfcheck      # 发布流水线的空目录冒烟自检：打印版本号后退出
 *   icarus-ii-sidecar --help | --version
 *
 * 约定（验收脚本依赖它）：
 *   - stdout **只**输出一行机器可读的启动信息：`READY {"port":8765,"token":"…"}`
 *   - 其余日志全部走 stderr（见 log.ts）
 *   - 启动后写 `<data-dir>/runtime.json`，退出时删除
 *
 * 密钥来源：`<data-dir>/config.json` 或运行中的 `config.set`（协议附录 A）。
 * **不从命令行读密钥**，也不会把密钥写进日志或 stdout。
 */

import { randomBytes } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { DEFAULT_PORT, PROTOCOL_VERSION, isLoopbackHost } from './protocol.js';
import { EchoAgent, PiAgent, type AgentRunner } from './agent.js';
import { CONFIG_FILE_NAME, ConfigStore, DEFAULT_MODEL, DEFAULT_PROVIDER } from './config.js';
import { errorFields, log, setLevel } from './log.js';
import { startServer, type RunningServer } from './server.js';
import { GameToolHost } from './tools/host.js';
import { KnowledgeManager } from './kb/manager.js';
import { KnowledgeStore } from './kb/store.js';
import { toolNamesForCapabilities } from './tools/registry.js';

/**
 * 打包成单文件（esbuild --bundle，见 scripts/build-sidecar.sh）后 `__dirname` 里没有
 * package.json，所以版本号内嵌一份，package.json 只用来交叉核对（不一致时警告）。
 */
const SERVER_VERSION = '1.2.384';

const DEFAULT_HOST = '127.0.0.1';
const DEFAULT_DATA_DIR_NAME = '.icarus-ii-dev';

export type AgentKind = 'echo' | 'pi';

/** ESM 下的 `__dirname`。esbuild 打包时由 --define/--banner 提供 import.meta.url。 */
const HERE = dirname(fileURLToPath(import.meta.url));

interface ServeArgs {
  host: string;
  port: number;
  dataDir: string;
  token: string;
  agentKind: AgentKind;
  provider: string;
  model: string;
  debugTools: boolean;
  /** 开发/评测用：整份替换内置人格（启动读一次）。 */
  systemPromptFile?: string;
}

function usage(): string {
  return [
    'icarus-ii sidecar (M1)',
    '',
    '用法:',
    '  icarus-ii-sidecar serve [选项]',
    '',
    '选项:',
    `  --agent <echo|pi> agent 实现（默认 pi；echo 为 M0 兼容实现，不需要密钥）`,
    `  --provider <id>   模型提供方（默认 ${DEFAULT_PROVIDER}）`,
    `  --model <id>      模型 id（默认 ${DEFAULT_MODEL}）`,
    `  --port <n>        监听端口（默认 ${DEFAULT_PORT}，0 = 随机端口）`,
    `  --host <addr>     监听地址（仅允许 ${DEFAULT_HOST} / ::1 / localhost / 127.*）`,
    `  --data-dir <path> 数据目录（默认 ~/${DEFAULT_DATA_DIR_NAME}）；写 runtime.json 与 config.json`,
    '  --token <token>   握手 token（默认随机 32 字节 hex）',
    '  --system-prompt-file <path>',
    '                    用文件整份替换内置人格（仅调试/评测；启动时读一次，文件缺失直接报错）',
    '  --debug-tools     开启 !tool 调试直调（不设工具白名单；默认关闭）',
    '  --log-level <l>   debug | info | warn | error（默认 info）',
    '  -h, --help        显示本帮助',
    '  -v, --version     显示版本号',
    '      --selfcheck   打印版本号并退出（发布流水线冒烟自检；不联网、不需要密钥）',
    '',
    '环境变量:',
    '',
    'API Key 通过 config.set（mod）或 <data-dir>/config.json 提供，不从命令行读取。',
  ].join('\n');
}

function readPackageVersion(): string | null {
  try {
    const pkgPath = join(HERE, '..', 'package.json');
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

function parseAgentKind(raw: string | undefined): AgentKind {
  if (raw === undefined) return 'pi';
  if (raw === 'pi' || raw === 'echo') return raw;
  throw new Error(`--agent 必须是 echo 或 pi，收到 "${raw}"`);
}

function requireNonEmpty(raw: string | undefined, flag: string, fallback: string): string {
  if (raw === undefined) return fallback;
  if (raw.trim().length === 0) throw new Error(`${flag} 不能为空`);
  return raw;
}

/**
 * 装配 agent。
 * `pi` 的 pi SDK 是懒加载的：这里只构造一个空壳，第一次真正需要模型时才 import。
 */
function createAgent(
  kind: AgentKind,
  store: ConfigStore,
  dataDir: string,
  host: GameToolHost,
  kb: KnowledgeStore,
  systemPromptFile?: string,
): AgentRunner {
  return kind === 'echo'
    ? new EchoAgent()
    : new PiAgent({ dataDir, store, host, kb, systemPromptFile });
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

  // 数据目录要先建出来：config.json 与 runtime.json 都落在这里。
  try {
    mkdirSync(args.dataDir, { recursive: true });
  } catch (err) {
    log.error('创建数据目录失败', { dataDir: args.dataDir, ...errorFields(err) });
    process.exitCode = 1;
    return;
  }

  const config = new ConfigStore({
    path: join(args.dataDir, CONFIG_FILE_NAME),
    defaults: { provider: args.provider, model: args.model },
    // echo 不需要凭证：ready.config.state 恒为 ready，prompt 不会被拦。
    requireApiKey: args.agentKind === 'pi',
  });
  const host = new GameToolHost();
  const kbStore = new KnowledgeStore(args.dataDir);
  const knowledge = new KnowledgeManager({ store: kbStore, logger: log });
  const agent = createAgent(
    args.agentKind,
    config,
    args.dataDir,
    host,
    kbStore,
    args.systemPromptFile,
  );

  let server: RunningServer;
  try {
    server = await startServer({
      host: args.host,
      port: args.port,
      token: args.token,
      version: SERVER_VERSION,
      agent,
      config,
      knowledge,
      // M2/M3：按 hello.capabilities 决定游戏工具；kb_* 始终注册。
      tools: toolNamesForCapabilities,
      debugTools: args.debugTools,
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
    agent.dispose();
    process.exitCode = 1;
    return;
  }

  log.info('sidecar 已就绪', {
    url: server.url,
    runtimeFile,
    version: SERVER_VERSION,
    protocol: PROTOCOL_VERSION,
    agent: `${agent.info.provider}/${agent.info.model}`,
    agentKind: args.agentKind,
    configState: config.view().state,
    apiKey: config.maskedApiKey,
    debugTools: args.debugTools,
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
    try {
      agent.dispose();
    } catch (err) {
      log.warn('释放 agent 时出错', errorFields(err));
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
  agent?: string;
  provider?: string;
  model?: string;
  'debug-tools'?: boolean;
  'system-prompt-file'?: string;
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
        agent: { type: 'string' },
        provider: { type: 'string' },
        model: { type: 'string' },
        'debug-tools': { type: 'boolean' },
        'system-prompt-file': { type: 'string' },
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
    // scripts/build-sidecar.sh 在空目录里跑这一条：不碰网络、不读密钥、不写文件，
    // 只证明 bundle 能启动。注意不要在这里构造 ConfigStore / agent。
    process.stdout.write(`icarus-ii-sidecar ${SERVER_VERSION} (protocol ${PROTOCOL_VERSION})\n`);
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
  let agentKind: AgentKind;
  let provider: string;
  let model: string;
  try {
    port = parsePort(values['port']);
    agentKind = parseAgentKind(values['agent']);
    provider = requireNonEmpty(values['provider'], '--provider', DEFAULT_PROVIDER);
    model = requireNonEmpty(values['model'], '--model', DEFAULT_MODEL);
  } catch (err) {
    process.stderr.write(`${(err as Error).message}\n`);
    process.exit(2);
    return;
  }

  const dataDir = expandHome(values['data-dir'] ?? join(homedir(), DEFAULT_DATA_DIR_NAME));
  const host = values['host'] ?? DEFAULT_HOST;
  if (!isLoopbackHost(host)) {
    process.stderr.write(`sidecar 只允许监听回环地址，收到 --host=${host}\n`);
    process.exit(2);
    return;
  }
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

  const systemPromptFile =
    values['system-prompt-file'] === undefined
      ? undefined
      : expandHome(values['system-prompt-file']);
  if (systemPromptFile !== undefined && systemPromptFile.trim().length === 0) {
    process.stderr.write('--system-prompt-file 不能为空\n');
    process.exit(2);
    return;
  }

  if (pkgVersion && pkgVersion !== SERVER_VERSION) {
    log.warn('内嵌版本号与 package.json 不一致', { embedded: SERVER_VERSION, package: pkgVersion });
  }

  log.info('启动中', {
    version: SERVER_VERSION,
    protocol: PROTOCOL_VERSION,
    agentKind,
    provider,
    model,
    host,
    port,
    dataDir,
    tokenLength: token.length,
    tokenGenerated: values['token'] === undefined,
    debugTools: values['debug-tools'] === true,
    systemPromptFile: systemPromptFile ?? null,
  });

  await runServe({
    host,
    port,
    dataDir,
    token,
    agentKind,
    provider,
    model,
    debugTools: values['debug-tools'] === true,
    systemPromptFile,
  });
}

void main(process.argv.slice(2)).catch((err: unknown) => {
  log.error('致命错误', errorFields(err));
  process.exit(1);
});
