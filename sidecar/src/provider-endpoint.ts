/**
 * 供应商端点探测：`model.catalog`（获取可用模型）与 `provider.test`（检测 Key/连通性）共用。
 *
 * 背景：这两条路径以前各写一套 URL/鉴权逻辑——模型目录固定用 OpenAI 的
 * `Authorization: Bearer`，Key 检测才按 provider 分 Anthropic/Google。结果是同一个
 * key + baseUrl 在「检测」里通过、在「获取可用模型」里被 401 拒绝；反过来，中转站
 * 用 x-api-key 时也一样。这里把下面四件事收敛到一处：
 *
 *   1. 地址清洗（补 scheme、去掉尾部 `/chat/completions` 之类的完整端点、去尾斜杠）；
 *   2. 鉴权风格判断（openai / anthropic / google / azure）；
 *   3. 失败回退（漏写 `/v1` 时补一次；鉴权风格不符时换 Bearer 再试一次）；
 *   4. 响应解析（OpenAI 的 `data[].id`、Anthropic 的 `data[].id`、Gemini 的
 *      `models[].name`，并把 Gemini 的 `models/` 前缀去掉）。
 *
 * 安全约定：key 只放在请求头里（Google 也用 `x-goog-api-key`，不拼进 query），
 * 返回给调用方的 `endpoint` 一律去掉 query/hash，避免 key 混进日志或错误文案。
 */

import type { ModelCatalogEntry } from './protocol.js';

export type ProviderStyle = 'openai' | 'anthropic' | 'google' | 'azure';

export interface ProbeAttempt {
  style: ProviderStyle;
  /** 真实请求地址（可能含非敏感 query，例如 `api-version`）。 */
  url: string;
  headers: Record<string, string>;
}

export interface ProbeOptions {
  provider: string;
  baseUrl: string;
  apiKey: string | null;
  /** pi 目录里的 `api` 字段（可选），用于更准确地判断风格。 */
  api?: string | null;
}

export interface ProbeResult {
  ok: boolean;
  status: number;
  latencyMs: number;
  style: ProviderStyle;
  /** 响应是否是合法 JSON（2xx 但返回 HTML 通常意味着被网关拦截）。 */
  json: boolean;
  body: unknown;
  /** 网络层错误（超时/无法连接）；有值时 `status` 通常为 0。 */
  error: string | null;
  /** 实际发起的请求数（1 表示没有走回退）。 */
  attempts: number;
  /** 最终结果对应的端点（已去掉 query/hash，可安全写日志）。 */
  endpoint: string;
  /**
   * 成功时返回「适合聊天使用」的 base URL（已按各家适配器的期望处理好版本段）。
   * 失败或 Azure 时为空串：调用方不要拿它去覆盖玩家的配置。
   */
  workingBaseUrl: string;
}

/** Azure OpenAI 的模型列表需要一个 `api-version`；用户没写时用这个兜底。 */
const AZURE_API_VERSION = '2024-10-21';

/** 首次探测允许较慢（跨境网络），回退尝试要快，整体不拖过 mod 的 20s 等待。 */
const PRIMARY_TIMEOUT_MS = 9000;
const FALLBACK_TIMEOUT_MS = 5000;
const TOTAL_BUDGET_MS = 17000;

const ENDPOINT_SUFFIX =
  /\/(chat\/completions|completions|responses|messages|embeddings|images\/generations)$/i;
const DEPLOYMENT_SUFFIX = /\/deployments\/[^/]+$/i;

/**
 * 清洗玩家填的「API 地址」。
 *
 * 常见的错误形态都来自复制文档：整段 `.../v1/chat/completions`、漏写 `https://`、
 * 尾部多一个 `/`。这些都会让 `${base}/models` 拼出 404。
 */
export function normalizeBaseUrl(raw: string): string {
  let value = raw.trim();
  if (value.length === 0) return '';
  if (!/^[a-z][a-z0-9+.-]*:\/\//i.test(value)) value = `https://${value}`;

  const match = /^([a-z][a-z0-9+.-]*):\/\/([^?#]*)(\?[^#]*)?(#.*)?$/i.exec(value);
  if (!match) return value.replace(/\/+$/, '');

  const scheme = (match[1] ?? 'https').toLowerCase();
  let path = (match[2] ?? '').replace(/\/+$/, '');
  path = path.replace(DEPLOYMENT_SUFFIX, '');
  path = path.replace(ENDPOINT_SUFFIX, '');
  path = path.replace(/\/+$/, '');
  const search = match[3] ?? '';
  return `${scheme}://${path}${search}`;
}

export function detectProviderStyle(
  provider: string,
  baseUrl: string,
  api: string | null = null,
): ProviderStyle {
  const id = provider.trim().toLowerCase();
  const base = baseUrl.toLowerCase();
  if (
    id === 'anthropic' ||
    id.startsWith('claude') ||
    /anthropic|claude\.ai/.test(base) ||
    Boolean(api && /anthropic/i.test(api))
  ) {
    return 'anthropic';
  }
  if (
    id === 'google' ||
    id.startsWith('google-') ||
    id === 'gemini' ||
    /generativelanguage|googleapis/.test(base) ||
    Boolean(api && /google/i.test(api))
  ) {
    return 'google';
  }
  if (/azure/.test(id) || /openai\.azure\.com/.test(base)) return 'azure';
  return 'openai';
}

/** 拆出 `?query`：路径拼装必须发生在 query 之前，否则 `?api-version=` 会被塞进路径。 */
function splitQuery(url: string): [string, string] {
  const index = url.indexOf('?');
  return index < 0 ? [url, ''] : [url.slice(0, index), url.slice(index + 1)];
}

function joinQuery(path: string, query: string): string {
  return query.length > 0 ? `${path}?${query}` : path;
}

/**
 * 把探测成功的地址修正成聊天适配器需要的 base：
 *   - Anthropic SDK 自己会补 `/v1/messages`，所以 base 不能再带 `/v1`；
 *   - pi 的 Gemini 适配器把 baseUrl 原样当版本路径（`apiVersion=""`），缺版本就补 `/v1beta`；
 *   - OpenAI 兼容保持原样（有的中转在根路径，有的在 `/v1`）。
 */
export function canonicalBaseForChat(
  provider: string,
  baseUrl: string,
  api: string | null = null,
): string {
  const base = normalizeBaseUrl(baseUrl);
  if (base.length === 0) return '';

  // `api` 是聊天真正会用的适配器：给了就以它为准。自定义供应商固定走
  // `openai-completions`，不能因为域名像 Gemini 就把 `/v1beta` 塞进 base。
  const style: ProviderStyle = api
    ? /anthropic/i.test(api)
      ? 'anthropic'
      : /google/i.test(api)
        ? 'google'
        : 'openai'
    : detectProviderStyle(provider, base, null);

  if (style === 'anthropic') return base.replace(/\/v1$/i, '');
  if (style === 'google') return isVersioned(base) ? base : `${base}/v1beta`;
  return base;
}

function isVersioned(base: string): boolean {
  const [path] = splitQuery(base);
  return /\/v\d+[a-z]*$/i.test(path) || /\/openai$/i.test(path) || /\/models$/i.test(path);
}

function withVersion(base: string, version: string): string {
  const [path, query] = splitQuery(base);
  return joinQuery(isVersioned(base) ? path : `${path}/${version}`, query);
}

function modelsUrl(base: string): string {
  const [path, query] = splitQuery(base);
  return joinQuery(/\/models$/i.test(path) ? path : `${path}/models`, query);
}

/** 手工拼 query，避免 `new URL` 对畸形主机名抛异常；已存在的参数不覆盖。 */
function appendQuery(url: string, params: Record<string, string>): string {
  const entries = Object.entries(params).filter(
    ([key]) => !new RegExp(`[?&]${key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}=`).test(url),
  );
  if (entries.length === 0) return url;
  const separator = url.includes('?') ? '&' : '?';
  const query = entries
    .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`)
    .join('&');
  return `${url}${separator}${query}`;
}

function headersFor(style: ProviderStyle, apiKey: string | null): Record<string, string> {
  const headers: Record<string, string> = {
    accept: 'application/json',
    // 部分中转/网关会拦掉 Node 默认 UA；带一个明确的客户端标识更容易放行。
    'user-agent': 'icarus-ii-sidecar',
  };
  if (!apiKey) return headers;
  if (style === 'anthropic') {
    headers['x-api-key'] = apiKey;
    headers['anthropic-version'] = '2023-06-01';
  } else if (style === 'google') {
    // Gemini 同时支持 `?key=` 与 `x-goog-api-key`；用头是为了不让 key 进 URL。
    headers['x-goog-api-key'] = apiKey;
  } else if (style === 'azure') {
    headers['api-key'] = apiKey;
  } else {
    headers.authorization = `Bearer ${apiKey}`;
  }
  return headers;
}

function buildAttempt(
  style: ProviderStyle,
  base: string,
  apiKey: string | null,
): ProbeAttempt | null {
  if (base.length === 0) return null;
  let url: string;
  if (style === 'anthropic') {
    url = appendQuery(modelsUrl(withVersion(base, 'v1')), { limit: '1000' });
  } else if (style === 'google') {
    url = appendQuery(modelsUrl(withVersion(base, 'v1beta')), { pageSize: '1000' });
  } else if (style === 'azure') {
    const [path, query] = splitQuery(base);
    const azureBase =
      /openai\.azure\.com/i.test(path) && !/\/openai(\/|$)/i.test(path)
        ? joinQuery(`${path}/openai`, query)
        : base;
    url = appendQuery(modelsUrl(azureBase), { 'api-version': AZURE_API_VERSION });
  } else {
    url = modelsUrl(base);
  }
  return { style, url, headers: headersFor(style, apiKey) };
}

/**
 * 生成探测尝试序列。
 *
 * OpenAI 兼容端点的标准位置是 `<base>/v1/models`（各家文档里的 base_url 基本都带 `/v1`）；
 * 少数中转只在根路径实现 `/models`。所以**没写版本段时先试 `/v1`**，再接根路径——
 * 某些中转的根 `/models` 在带鉴权时会挂住，先试标准位置能少等一轮超时。
 * Anthropic/Google 的主尝试本身会补版本段；非 OpenAI 风格再加一次 Bearer 回退。
 */
export function buildProbeAttempts(options: ProbeOptions): ProbeAttempt[] {
  const base = normalizeBaseUrl(options.baseUrl);
  if (base.length === 0) return [];

  const style = detectProviderStyle(options.provider, base, options.api ?? null);
  const attempts: ProbeAttempt[] = [];
  const push = (candidate: ProviderStyle, url: string): void => {
    const attempt = buildAttempt(candidate, url, options.apiKey);
    if (attempt) attempts.push(attempt);
  };

  if (style === 'openai' && !isVersioned(base)) {
    push('openai', `${base}/v1`);
    push('openai', base);
  } else {
    push(style, base);
    if (style !== 'openai') push('openai', base);
  }
  return attempts.slice(0, 3);
}

function listFrom(body: unknown): unknown[] {
  if (Array.isArray(body)) return body;
  if (!body || typeof body !== 'object') return [];
  const obj = body as Record<string, unknown>;
  for (const key of ['data', 'models', 'result']) {
    const value = obj[key];
    if (Array.isArray(value)) return value;
  }
  const data = obj['data'];
  if (data && typeof data === 'object') {
    const nested = (data as Record<string, unknown>)['models'];
    if (Array.isArray(nested)) return nested;
  }
  return [];
}

function stripModelsPrefix(id: string): string {
  return id.replace(/^models\//i, '');
}

/** 解析各家的模型列表；Gemini 的 `models/xxx` 前缀会被去掉（聊天要用裸 id）。 */
export function parseModelList(body: unknown): ModelCatalogEntry[] {
  const seen = new Set<string>();
  const out: ModelCatalogEntry[] = [];

  for (const item of listFrom(body)) {
    if (typeof item === 'string') {
      const id = stripModelsPrefix(item.trim());
      if (!id || seen.has(id)) continue;
      seen.add(id);
      out.push({ id, name: id });
      continue;
    }
    if (!item || typeof item !== 'object') continue;

    const obj = item as Record<string, unknown>;
    const idField = typeof obj['id'] === 'string' ? obj['id'] : '';
    const nameField = typeof obj['name'] === 'string' ? obj['name'] : '';
    const modelField = typeof obj['model'] === 'string' ? obj['model'] : '';
    const id = stripModelsPrefix((idField || nameField || modelField).trim());
    if (!id || seen.has(id)) continue;

    const display =
      (typeof obj['displayName'] === 'string' && obj['displayName']) ||
      (typeof obj['display_name'] === 'string' && obj['display_name']) ||
      (idField && nameField ? nameField : '');

    seen.add(id);
    out.push({ id, name: display.trim() || id });
  }
  return out;
}

async function readJson(response: Response): Promise<{ json: boolean; body: unknown }> {
  let text: string;
  try {
    text = await response.text();
  } catch {
    return { json: false, body: null };
  }
  if (text.trim().length === 0) return { json: false, body: null };
  try {
    return { json: true, body: JSON.parse(text) as unknown };
  } catch {
    return { json: false, body: null };
  }
}

/** 返回给日志/错误文案的端点：去掉 query 与 hash（Google 的 key 可能在 query 里）。 */
export function safeEndpoint(url: string): string {
  try {
    const parsed = new URL(url);
    return `${parsed.host}${parsed.pathname}`;
  } catch {
    return url.replace(/[?#].*$/, '');
  }
}

function describeNetworkError(err: unknown): string {
  const raw = err instanceof Error ? err.message : String(err);
  if (/timeout|timed out|aborted/i.test(raw)) return '检测超时';
  if (/fetch failed|ENOTFOUND|ECONNREFUSED|EAI_AGAIN|ETIMEDOUT/i.test(raw)) {
    return '网络错误：连不上端点（需要代理时先设置 HTTPS_PROXY）';
  }
  return `网络错误：${raw}`;
}

/**
 * 依次尝试候选端点，返回第一个 2xx 且为 JSON 的结果。
 *
 * 只有 401/403/404 才继续回退：429/5xx/网络错误立即停止，避免无意义的重复请求。
 */
export async function probeModelEndpoint(options: ProbeOptions): Promise<ProbeResult> {
  const started = Date.now();
  const attempts = buildProbeAttempts(options);
  if (attempts.length === 0) {
    return {
      ok: false,
      status: 0,
      latencyMs: 0,
      style: 'openai',
      json: false,
      body: null,
      error: 'API 地址无效',
      attempts: 0,
      endpoint: '',
      workingBaseUrl: '',
    };
  }

  let lastStatus = 0;
  let lastError: string | null = null;
  let lastStyle: ProviderStyle = attempts[0]?.style ?? 'openai';
  let lastEndpoint = '';
  let seen = 0;

  for (let index = 0; index < attempts.length; index += 1) {
    const attempt = attempts[index];
    if (!attempt) break;
    const remaining = TOTAL_BUDGET_MS - (Date.now() - started);
    if (remaining <= 0) {
      lastError = lastError ?? '检测超时';
      break;
    }

    const timeout = Math.min(index === 0 ? PRIMARY_TIMEOUT_MS : FALLBACK_TIMEOUT_MS, remaining);
    seen = index + 1;
    lastStyle = attempt.style;
    lastEndpoint = safeEndpoint(attempt.url);

    try {
      const response = await fetch(attempt.url, {
        headers: attempt.headers,
        redirect: 'follow',
        signal: AbortSignal.timeout(timeout),
      });
      lastStatus = response.status;

      if (response.ok) {
        const parsed = await readJson(response);
        if (!parsed.json) {
          return {
            ok: false,
            status: response.status,
            latencyMs: Date.now() - started,
            style: attempt.style,
            json: false,
            body: null,
            error: '端点返回的不是 JSON（可能被网关或代理拦截）',
            attempts: index + 1,
            endpoint: lastEndpoint,
            workingBaseUrl: '',
          };
        }
        const [probePath] = splitQuery(attempt.url);
        const probeBase = probePath.replace(/\/models$/i, '');
        return {
          ok: true,
          status: response.status,
          latencyMs: Date.now() - started,
          style: attempt.style,
          json: true,
          body: parsed.body,
          error: null,
          attempts: index + 1,
          endpoint: lastEndpoint,
          // 给 mod 一个「聊天也会用」的地址：玩家把地址写错时（缺 /v1、多 /v1）
          // 可以在检测/取目录成功后自动补全，避免「检测通过、对话 404」。
          workingBaseUrl:
            attempt.style === 'azure'
              ? ''
              : canonicalBaseForChat(options.provider, probeBase, options.api ?? null),
        };
      }

      // 鉴权/路径不对才值得换一种写法重试；限流与服务端错误重试也只会更糟。
      if (response.status !== 401 && response.status !== 403 && response.status !== 404) break;
    } catch (err) {
      lastError = describeNetworkError(err);
      // 超时/连接失败也换下一个候选再试：部分中转的 `/models` 会挂住，
      // 但带版本段的 `/v1/models` 正常。预算不够就停，绝不拖过 mod 的等待上限。
      const remainingAfter = TOTAL_BUDGET_MS - (Date.now() - started);
      if (index + 1 >= attempts.length || remainingAfter < 2500) break;
    }
  }

  return {
    ok: false,
    status: lastStatus,
    latencyMs: Date.now() - started,
    style: lastStyle,
    json: false,
    body: null,
    error: lastError,
    attempts: seen,
    endpoint: lastEndpoint,
    workingBaseUrl: '',
  };
}

/** 把探测失败翻译成玩家能理解的一句话（确认卡/设置页都会直接显示）。 */
export function describeProbeFailure(result: ProbeResult): string {
  if (result.error) return result.error;
  switch (result.status) {
    case 401:
    case 403:
      return `API Key 被拒绝（HTTP ${result.status}）`;
    case 404:
      return '端点没有 /models（HTTP 404，可能缺 /v1）';
    case 429:
      return '请求被限流（HTTP 429）';
    default:
      return result.status > 0 ? `端点返回 HTTP ${result.status}` : '无法连接端点';
  }
}
