/**
 * 中英 DSP Wiki 工具（M4g）。
 *
 * `wiki_search` 找页面，`wiki_page` 取正文。两者都只读、都带来源 URL；
 * 结果必须转述并在回答里给出链接，不能拿模型记忆替代 wiki 内容。
 */

import type { ToolDefinition } from '@earendil-works/pi-coding-agent';
import { Type } from 'typebox';
import { WikiClient, type WikiLang } from './client.js';

export const WIKI_TOOL_NAMES = ['wiki_search', 'wiki_page'] as const;

const MAX_PAGE_CHARS = 8000;

type ToolReply = { content: { type: 'text'; text: string }[]; details: unknown };

function reply(payload: unknown, ok: boolean, code?: string): ToolReply {
  const text = JSON.stringify(payload, null, 2);
  return {
    content: [
      {
        type: 'text',
        text: ok ? text : `[${code ?? 'wiki_error'}] ${text}`,
      },
    ],
    details: payload,
  };
}

function resolveLangs(value: unknown): WikiLang[] {
  if (value === 'zh') return ['zh'];
  if (value === 'en') return ['en'];
  return ['zh', 'en'];
}

export function createWikiTools(client: WikiClient = new WikiClient()): ToolDefinition[] {
  return [
    {
      name: 'wiki_search',
      label: 'Wiki 搜索',
      description:
        '在中/英文戴森球计划 Wiki 里搜索页面，返回标题、摘要与链接。' +
        '用于查游戏机制、版本改动、物品/建筑的百科说明；结果必须附上链接，不能凭记忆替代。',
      promptGuidelines: [
        'Use wiki_search 当玩家问机制、版本差异、百科背景，而 kb_* 没有覆盖时。',
        '回答里保留 wiki 链接（url），并说明信息来自哪个语言的 wiki。',
        'wiki 链接必须原样使用工具返回的 url；不要自行拼接或改写成 /wiki/、/Wiki/。',
        'wiki 没有结果或站点不可用时，直说“wiki 没查到/暂时不可用”，不要改用记忆编造。',
      ],
      parameters: Type.Object({
        query: Type.String({ description: '搜索词，例如“增产剂”“黑雾”“物流塔”' }),
        lang: Type.Optional(
          Type.Union([Type.Literal('zh'), Type.Literal('en'), Type.Literal('both')], {
            description: 'zh / en / both（默认 both）',
          }),
        ),
        limit: Type.Optional(
          Type.Integer({ minimum: 1, maximum: 10, description: '每种语言返回条数，默认 5' }),
        ),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const query = typeof args['query'] === 'string' ? args['query'] : '';
        if (!query.trim()) return reply({ error: 'query 不能为空' }, false, 'bad_request');

        const langs = resolveLangs(args['lang']);
        const limitRaw = typeof args['limit'] === 'number' ? args['limit'] : 5;
        const limit = Math.max(1, Math.min(10, Math.floor(limitRaw)));

        const { hits, errors } = await client.search(query, langs, limit);
        if (hits.length === 0) {
          if (errors.length === langs.length) {
            return reply(
              { error: 'wiki_unavailable', details: errors, query },
              false,
              'wiki_unavailable',
            );
          }
          return reply({ query, hits: [], errors, note: '没有匹配的页面' }, true);
        }

        return reply(
          {
            query,
            hits,
            errors,
            note: '内容来自对应 Wiki；回答时请原样使用返回的 url（不要自行拼 /wiki/），授权以站点页面声明为准。',
          },
          true,
        );
      },
    },

    {
      name: 'wiki_page',
      label: 'Wiki 页面',
      description:
        '读取一个中/英文 DSP Wiki 页面的纯文本正文（自动处理重定向，最长 8000 字）。' +
        '适合在 wiki_search 之后展开细节；必须附上返回的 url。',
      promptGuidelines: [
        'Use wiki_page 在 wiki_search 找到标题之后，读取正文细节。',
        '长度超过 8000 字会被截断（truncated=true）；不要假装读过截断之后的内容。',
        'wiki 链接必须原样使用返回的 url；不要自行拼接或改写成 /wiki/、/Wiki/。',
        '页面不存在时如实说“wiki 没有这个页面”，不要用记忆补。',
      ],
      parameters: Type.Object({
        title: Type.String({ description: '页面标题（wiki_search 返回的 title）' }),
        lang: Type.Optional(
          Type.Union([Type.Literal('zh'), Type.Literal('en')], { description: '默认 zh' }),
        ),
      }),
      async execute(_id, params) {
        const args = (params ?? {}) as Record<string, unknown>;
        const title = typeof args['title'] === 'string' ? args['title'] : '';
        if (!title.trim()) return reply({ error: 'title 不能为空' }, false, 'bad_request');
        const lang: WikiLang = args['lang'] === 'en' ? 'en' : 'zh';

        const { page, error } = await client.page(title, lang);
        if (!page) {
          const code = error === 'not_found' ? 'wiki_not_found' : 'wiki_unavailable';
          return reply({ title, lang, error: error ?? 'unknown' }, false, code);
        }
        if (page.extract.length > MAX_PAGE_CHARS) {
          // client 已经截过；这里只做防御。
          page.extract = page.extract.slice(0, MAX_PAGE_CHARS);
          page.truncated = true;
        }
        return reply(page, true);
      },
    },
  ];
}
