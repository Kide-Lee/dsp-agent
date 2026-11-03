/**
 * sidecar 的网络层：一个 `node:http` 服务（只有 `GET /health`）+ 挂在其上的 WebSocket。
 *
 * - 不引入 Fastify/Express：M0 只有一条健康检查路由，内置 http 足够；
 *   M1 需要路由时再换（design.md §12 已经把那一层留好了）。
 * - `/health` **不需要鉴权**：只暴露版本与 uptime，不含 token 或会话内容。
 * - WebSocket 走 `noServer` 模式，自己处理 `upgrade`，这样路径与握手时机完全可控。
 */

import { createServer, type IncomingMessage, type Server, type ServerResponse } from 'node:http';
import type { Duplex } from 'node:stream';
import { WebSocketServer, type RawData, type WebSocket } from 'ws';
import { CLOSE_GOING_AWAY, MAX_FRAME_BYTES, isLoopbackHost } from './protocol.js';
import type { AgentRunner } from './agent.js';
import type { ConfigStore } from './config.js';
import type { KnowledgeManager } from './kb/manager.js';
import { errorFields, log as rootLog } from './log.js';
import { Session, type SocketLike } from './session.js';
import { GameLockManager } from './tools/locks.js';

/**
 * 传输层硬上限：给应用层 8 MiB 判定留出余量。
 * 8 MiB 以内的帧由 Session 判定；8–32 MiB 之间我们还能回一条
 * `error{frame_too_large}` 再关；超过 32 MiB 只能由 ws 直接断开（记录日志）。
 */
const TRANSPORT_HARD_FRAME_BYTES = MAX_FRAME_BYTES * 4;

/** 优雅关闭时给对端发 close frame 的宽限期，超时就 terminate。 */
const SHUTDOWN_GRACE_MS = 1000;

export interface ServerOptions {
  host: string;
  /** 0 表示由系统分配端口（测试用）。 */
  port: number;
  token: string;
  version: string;
  /** 本次进程的 agent（所有连接共享）。 */
  agent: AgentRunner;
  /** 配置存储（所有连接共享）。 */
  config: ConfigStore;
  /** 知识库编排（所有连接共享）。 */
  knowledge?: KnowledgeManager;
  /** 工具名列表；给出函数时按 hello 能力动态计算。 */
  tools?: readonly string[] | ((capabilities: readonly string[]) => readonly string[]);
  /** M4m：是否开启 `tool_debug` 调试直调（默认 false）。 */
  debugTools?: boolean;
  helloTimeoutMs?: number;
}

export interface RunningServer {
  readonly host: string;
  readonly port: number;
  readonly url: string;
  connectionCount(): number;
  close(): Promise<void>;
}

function toBuffer(data: RawData): Buffer {
  if (Buffer.isBuffer(data)) return data;
  if (Array.isArray(data)) return Buffer.concat(data);
  return Buffer.from(data);
}

function sendJson(res: ServerResponse, status: number, body: unknown): void {
  const text = JSON.stringify(body);
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(text),
    'cache-control': 'no-store',
  });
  res.end(text);
}

function createSocketAdapter(ws: WebSocket, peer: string): SocketLike {
  return {
    get isOpen(): boolean {
      return ws.readyState === ws.OPEN;
    },
    send: (text, cb) => ws.send(text, (err?: Error) => cb?.(err)),
    close: (code, reason) => ws.close(code, reason),
    peer,
  };
}

export async function startServer(options: ServerOptions): Promise<RunningServer> {
  const logger = rootLog.child({ component: 'server' });
  const startedAt = Date.now();
  const tools = options.tools ?? [];

  if (!isLoopbackHost(options.host)) {
    throw new Error(`sidecar 只允许监听回环地址，收到 host=${options.host}`);
  }

  const httpServer: Server = createServer((req: IncomingMessage, res: ServerResponse) => {
    const method = req.method ?? 'GET';
    const path = (req.url ?? '/').split('?')[0] ?? '/';

    if (path === '/health') {
      if (method !== 'GET' && method !== 'HEAD') {
        res.setHeader('allow', 'GET, HEAD');
        sendJson(res, 405, { status: 'error', error: 'method_not_allowed' });
        return;
      }
      sendJson(res, 200, {
        status: 'ok',
        version: options.version,
        uptimeMs: Date.now() - startedAt,
      });
      return;
    }

    sendJson(res, 404, { status: 'error', error: 'not_found' });
  });

  const wss = new WebSocketServer({
    noServer: true,
    perMessageDeflate: false,
    maxPayload: TRANSPORT_HARD_FRAME_BYTES,
  });

  const sessions = new Set<Session>();
  const gameLocks = new GameLockManager();

  httpServer.on('upgrade', (req: IncomingMessage, socket: Duplex, head: Buffer) => {
    socket.on('error', (err) => {
      logger.debug('upgrade socket 出错', errorFields(err));
    });
    wss.handleUpgrade(req, socket, head, (ws) => {
      wss.emit('connection', ws, req);
    });
  });

  wss.on('connection', (ws: WebSocket, req: IncomingMessage) => {
    const peer = req.socket.remoteAddress ?? 'unknown';
    const session = new Session(createSocketAdapter(ws, peer), {
      token: options.token,
      serverVersion: options.version,
      agent: options.agent,
      config: options.config,
      knowledge: options.knowledge,
      tools,
      debugTools: options.debugTools,
      helloTimeoutMs: options.helloTimeoutMs,
      locks: gameLocks,
      logger: rootLog,
    }, () => {
      sessions.delete(session);
    });

    sessions.add(session);
    logger.info('连接建立', { peer, total: sessions.size, path: req.url ?? '/' });

    ws.on('message', (data: RawData, isBinary: boolean) => {
      session.handleFrame(toBuffer(data), isBinary);
    });

    ws.on('close', (code: number, reason: Buffer) => {
      session.dispose(`peer close ${code}${reason.length ? ` ${reason.toString('utf8')}` : ''}`);
      sessions.delete(session);
    });

    ws.on('error', (err: Error) => {
      logger.warn('WebSocket 错误', { sessionId: session.sessionId, ...errorFields(err) });
      // ws 在超限帧等情况下会先 emit error 再关闭；这里只做记录与清理兜底。
      if (!session.isReady && session.state !== 'closed') session.dispose('websocket error');
    });
  });

  wss.on('error', (err: Error) => {
    logger.error('WebSocketServer 错误', errorFields(err));
  });

  await new Promise<void>((resolve, reject) => {
    const onError = (err: Error): void => {
      httpServer.off('listening', onListening);
      reject(err);
    };
    const onListening = (): void => {
      httpServer.off('error', onError);
      resolve();
    };
    httpServer.once('error', onError);
    httpServer.once('listening', onListening);
    httpServer.listen(options.port, options.host);
  });

  // 监听成功之后的错误只记录，不抛出。
  httpServer.on('error', (err: Error) => {
    logger.error('HTTP 服务错误', errorFields(err));
  });

  const address = httpServer.address();
  const actualPort = typeof address === 'object' && address ? address.port : options.port;

  logger.info('已开始监听', { host: options.host, port: actualPort });

  let closing: Promise<void> | null = null;

  const running: RunningServer = {
    host: options.host,
    port: actualPort,
    url: `ws://${options.host}:${actualPort}`,
    connectionCount: () => sessions.size,
    close(): Promise<void> {
      if (closing) return closing;
      closing = (async () => {
        logger.info('开始关闭', { sessions: sessions.size });
        for (const session of sessions) {
          session.closeWith(CLOSE_GOING_AWAY, 'server shutting down');
        }

        // 对端不回应 close frame 时不能无限等：到点强制断开。
        const forceTimer = setTimeout(() => {
          logger.warn('宽限期已过，强制断开剩余连接', { remaining: wss.clients.size });
          for (const client of wss.clients) {
            try {
              client.terminate();
            } catch (err) {
              logger.debug('terminate 失败', errorFields(err));
            }
          }
        }, SHUTDOWN_GRACE_MS);
        forceTimer.unref?.();

        await new Promise<void>((resolve) => {
          wss.close(() => resolve());
        });
        await new Promise<void>((resolve) => {
          httpServer.close(() => resolve());
        });

        clearTimeout(forceTimer);
        logger.info('已关闭');
      })();
      return closing;
    },
  };

  return running;
}
