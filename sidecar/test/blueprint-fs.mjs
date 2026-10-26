#!/usr/bin/env node
/**
 * blueprint_fs（蓝图目录虚拟文件系统）测试。
 *
 * 覆盖：pwd/cd/ls/cat/mkdir/write/rm/rmdir、默认 IcarusII、路径逃逸、
 * 永久删除、符号链接拒绝、write 的格式校验与卡片提取。
 */

import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';
import { BlueprintWorkspace } from '../dist/blueprint/workspace.js';
import { createBlueprintTools } from '../dist/blueprint/tools.js';
import { extractBlueprintCard } from '../dist/blueprint/card.js';

let passed = 0;
let failed = 0;
const ok = (name) => { passed++; console.log(`  ok   ${name}`); };
const fail = (name, detail) => { failed++; console.log(`  FAIL ${name}${detail ? `：${detail}` : ''}`); };
const check = (name, cond, detail) => (cond ? ok(name) : fail(name, detail));

const root = mkdtempSync(join(tmpdir(), 'icarus-ii-fs-'));
const workspace = new BlueprintWorkspace({ root });
const tools = createBlueprintTools(undefined, undefined, undefined, undefined, workspace);
const fsTool = tools.find((tool) => tool.name === 'blueprint_fs');
if (!fsTool) {
  console.error('缺少 blueprint_fs');
  process.exit(1);
}

let confirmRequest = null;
const denyFs = createBlueprintTools(
  undefined,
  undefined,
  undefined,
  undefined,
  workspace,
  undefined,
  async (request) => {
    confirmRequest = request;
    return false;
  },
).find((tool) => tool.name === 'blueprint_fs');

async function run(params) {
  const result = await fsTool.execute('fs-1', params);
  return result;
}

function details(result) {
  return result.details ?? {};
}

const payload = gzipSync(Buffer.from('blueprint-binary'));
const validText = `BLUEPRINT:0,10,0,0,0,0,0,0,0,0,0,0,0"${payload.toString('base64')}"${'a'.repeat(32)}`;

try {
  console.log('── 1. pwd / mkdir / cd ──');
  {
    const pwd = details(await run({ op: 'pwd' }));
    check('pwd 返回根', pwd.root === root, JSON.stringify(pwd));
    check('pwd 默认 IcarusII', pwd.defaultDir === '/IcarusII', String(pwd.defaultDir));

    const made = details(await run({ op: 'mkdir', path: 'IcarusII/产线/测试' }));
    check('mkdir 递归创建', made.created === '/IcarusII/产线/测试', String(made.created));
    check('真实目录存在', existsSync(join(root, 'IcarusII/产线/测试')));

    const cd = details(await run({ op: 'cd', path: 'IcarusII/产线' }));
    check('cd 进入目录', cd.cwd === '/IcarusII/产线', String(cd.cwd));
    const cwd = details(await run({ op: 'pwd' }));
    check('pwd 反映 cwd', cwd.cwd === '/IcarusII/产线', String(cwd.cwd));
  }

  console.log('\n── 2. write / ls / cat ──');
  {
    const written = details(await run({ op: 'write', path: '/IcarusII/产线/测试蓝图.txt', content: validText }));
    check('write 成功', written.saved === true, JSON.stringify(written));
    check('write 到绝对虚拟路径', written.relPath === 'IcarusII/产线/测试蓝图.txt', String(written.relPath));
    check('write 文件存在', existsSync(join(root, written.relPath)));

    const nonOverwrite = await run({ op: 'write', path: '/IcarusII/产线/测试蓝图.txt', content: validText });
    check('默认不覆盖', nonOverwrite.content[0].text.includes('[exists]'), nonOverwrite.content[0].text.slice(0, 100));
    const overwrite = details(await run({ op: 'write', path: '/IcarusII/产线/测试蓝图.txt', content: validText, overwrite: true }));
    check('overwrite=true 覆盖', overwrite.saved === true);

    const ls = details(await run({ op: 'ls', path: '/IcarusII/产线', details: true }));
    check('ls 列出文件', ls.entries.some((entry) => entry.name === '测试蓝图.txt'), JSON.stringify(ls.entries));
    const entry = ls.entries.find((row) => row.name === '测试蓝图.txt');
    check('ls details 解析蓝图', entry?.blueprint?.valid === true, JSON.stringify(entry));

    const cat = details(await run({ op: 'cat', path: '/IcarusII/产线/测试蓝图.txt' }));
    check('cat 返回元数据', cat.valid === true && cat.relPath === 'IcarusII/产线/测试蓝图.txt', JSON.stringify(cat));
    check('cat 默认不含原文', cat.raw === undefined);

    const raw = details(await run({ op: 'cat', path: '/IcarusII/产线/测试蓝图.txt', raw: true }));
    check('cat raw 返回原文', raw.raw === validText);
  }

  console.log('\n── 3. 路径安全 ──');
  {
    await run({ op: 'cd', path: '/' });
    const escape = await run({ op: 'cat', path: '..' });
    check('.. 逃逸被拒绝', escape.content[0].text.includes('[path_escape]'), escape.content[0].text.slice(0, 100));

    const abs = await run({ op: 'cat', path: '/etc/passwd' });
    check('绝对路径按虚拟根处理', abs.content[0].text.includes('[not_found]'), abs.content[0].text.slice(0, 100));

    const drive = await run({ op: 'cat', path: 'C:/Windows/win.ini' });
    check('盘符被拒绝', drive.content[0].text.includes('[invalid_path]'), drive.content[0].text.slice(0, 100));

    const outside = join(root, '..', 'outside-target');
    mkdirSync(outside, { recursive: true });
    try {
      symlinkSync(outside, join(root, 'IcarusII', 'link-out'), 'dir');
      const linked = await run({ op: 'ls', path: '/IcarusII/link-out' });
      check('符号链接被拒绝', linked.content[0].text.includes('[symlink_not_allowed]'), linked.content[0].text.slice(0, 100));
    } catch {
      ok('符号链接测试跳过（平台不支持）');
    } finally {
      try { rmSync(outside, { recursive: true, force: true }); } catch {}
    }

    const invalid = await run({ op: 'write', path: 'IcarusII/坏.txt', content: 'not a blueprint' });
    check('write 拒绝非蓝图', invalid.content[0].text.includes('[blueprint_invalid]'), invalid.content[0].text.slice(0, 100));
  }

  console.log('\n── 4. 永久删除 ──');
  {
    const noConfirm = await run({ op: 'rm', path: '/IcarusII/产线/测试蓝图.txt' });
    check('rm 需要 confirm', noConfirm.content[0].text.includes('[confirmation_required]'), noConfirm.content[0].text.slice(0, 100));
    check('未确认不删除', existsSync(join(root, 'IcarusII/产线/测试蓝图.txt')));

    const removed = details(await run({ op: 'rm', path: '/IcarusII/产线/测试蓝图.txt', confirm: true }));
    check('rm 永久删除', removed.removed === true && !existsSync(join(root, 'IcarusII/产线/测试蓝图.txt')));
    check('没有 .trash', !existsSync(join(root, '.trash')));

    const nonEmpty = await run({ op: 'rmdir', path: '/IcarusII/产线', confirm: true });
    check('rmdir 非空需 recursive', nonEmpty.content[0].text.includes('[not_empty]'), nonEmpty.content[0].text.slice(0, 100));

    const removedDir = details(await run({ op: 'rmdir', path: '/IcarusII/产线', recursive: true, confirm: true }));
    check('rmdir 递归删除', removedDir.removed === true && !existsSync(join(root, 'IcarusII/产线')));

    const rootDelete = await run({ op: 'rmdir', path: '/', recursive: true, confirm: true });
    check('不能删除蓝图根', rootDelete.content[0].text.includes('[root_protected]'), rootDelete.content[0].text.slice(0, 100));
  }

  console.log('\n── 5. 权限确认 ──');
  {
    const denied = await denyFs.execute('fs-deny', {
      op: 'write',
      path: '/IcarusII/denied.txt',
      content: validText,
    });
    check('写蓝图前请求确认', confirmRequest?.tool === 'blueprint_fs', JSON.stringify(confirmRequest));
    check('蓝图写确认 risk=1', confirmRequest?.risk === 1, String(confirmRequest?.risk));
    check(
      '拒绝后返回 confirmation_denied',
      denied.content[0].text.includes('[confirmation_denied]'),
      denied.content[0].text.slice(0, 100),
    );
    check('拒绝后不落盘', !existsSync(join(root, 'IcarusII/denied.txt')));
  }

  console.log('\n── 6. 卡片提取 ──');
  {
    const result = await run({ op: 'write', path: '/IcarusII/卡片.txt', content: validText });
    const card = extractBlueprintCard('blueprint_fs', result);
    check('blueprint_fs write 发卡片', card !== null, JSON.stringify(result.details));
    check('卡片 source=written', card?.source === 'written', String(card?.source));
    check('卡片带 relPath', card?.relPath === 'IcarusII/卡片.txt', String(card?.relPath));
  }
} finally {
  rmSync(root, { recursive: true, force: true });
}

if (failed > 0) {
  console.error(`\n❌ blueprint-fs 测试失败：${failed} 项`);
  process.exit(1);
}
console.log(`\n✅ blueprint-fs 测试全部通过（${passed} 项）`);
