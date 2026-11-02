#!/usr/bin/env node
// 检查（并按需更新）IcarusII 的第三方依赖版本。
//
//   node scripts/check-deps.mjs            # 只检查；有待更新项时退出码 1
//   node scripts/check-deps.mjs --update   # 把同大版本的更新项升到最新
//   node scripts/check-deps.mjs --update --major   # 连大版本一起升（Node 运行时同样是手动决定）
//
// 退出码：0 = 全部最新或已更新；1 = 仍有待更新项；2 = 运行错误（网络/命令失败）。
//
// 依赖来源：
//   - sidecar/package.json 的 dependencies / devDependencies
//   - scripts/build-sidecar.sh 里固定的 NODE_VERSION / ESBUILD_VERSION / POSTJECT_VERSION
//   - scripts/fetch-deps.sh 里固定的 BEPINEX_VERSION（**刻意锁定**，只报告不更新）
//   - dsp-handcraft/ 的参考源码（**只读参考**，只比对上游 master，不阻塞、不自动拉取）
// 未纳入：UnityPy（tools/build-kb.py 的开发期工具，未锁定版本）、dsp-game/Packages（跟随游戏的 Unity 版本）。

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const SIDECAR_DIR = resolve(ROOT, 'sidecar');
const BUILD_SIDECAR = resolve(ROOT, 'scripts/build-sidecar.sh');
const FETCH_DEPS = resolve(ROOT, 'scripts/fetch-deps.sh');
const FETCH_TIMEOUT_MS = 20_000;

// 蓝图生成器的参考源码：只读参考，不构建、不打包，代码也不能复制进本仓库（上游 GPL-3.0）。
// 只提示上游有新提交，不阻塞打包。
const REFERENCE_DIR = resolve(ROOT, 'dsp-handcraft');
const REFERENCE_REPO = 'zoujack018/dsp-handcraft';
const REFERENCE_BRANCH = 'master';
const REFERENCE_CLONE = `git clone https://github.com/${REFERENCE_REPO}.git dsp-handcraft`;

const args = process.argv.slice(2);
const doUpdate = args.includes('--update');
const allowMajor = args.includes('--major');
const unknown = args.filter((a) => !['--update', '--major', '--check'].includes(a));
if (unknown.length > 0) {
  console.error(`未知参数：${unknown.join(' ')}`);
  process.exit(2);
}

// 既有版本号（可能带预发布后缀，如 1.0.0-alpha.6）按 [major, minor, patch] 解析。
function parse(text) {
  const match = /^(\d+)\.(\d+)\.(\d+)/.exec(String(text).trim().replace(/^v/i, ''));
  if (!match) return null;
  return { major: Number(match[1]), minor: Number(match[2]), patch: Number(match[3]) };
}

function compare(a, b) {
  return a.major - b.major || a.minor - b.minor || a.patch - b.patch;
}

function format(version) {
  return `v${version.major}.${version.minor}.${version.patch}`;
}

async function fetchJson(url, headers = {}) {
  const response = await fetch(url, {
    headers: { 'user-agent': 'icarus-ii-check-deps', ...headers },
    signal: AbortSignal.timeout(FETCH_TIMEOUT_MS),
  });
  if (!response.ok) throw new Error(`HTTP ${response.status} ${url}`);
  return response.json();
}

// 取某个包的同大版本最新版与总体最新版（都是稳定版，忽略预发布）。
async function npmVersions(pkg, current) {
  const data = await fetchJson(`https://registry.npmjs.org/${pkg.replace('/', '%2F')}`, {
    accept: 'application/vnd.npm.install-v1+json',
  });
  const versions = Object.keys(data.versions ?? {});
  // 当前版本本身是预发布时（如 postject 只有 alpha 线），就把预发布也算进候选。
  const isPrerelease = current.includes('-');
  const currentVersion = parse(current);
  const candidates = versions.filter((v) => isPrerelease || !v.includes('-'));
  const sameMajor = candidates
    .map(parse)
    .filter((v) => v && v.major === currentVersion.major)
    .sort(compare);
  const all = candidates.map(parse).filter(Boolean).sort(compare);
  return {
    sameMajor: sameMajor.length > 0 ? format(sameMajor.at(-1)) : null,
    latest: all.length > 0 ? format(all.at(-1)) : null,
  };
}

async function nodeVersions() {
  const releases = await fetchJson('https://nodejs.org/dist/index.json');
  const stable = releases
    .filter((r) => !r.version.includes('-'))
    .map((r) => ({ ...r, parsed: parse(r.version.slice(1)) }))
    .filter((r) => r.parsed);
  const lts = stable.filter((r) => typeof r.lts === 'string');
  const latestLts = lts.sort((a, b) => compare(a.parsed, b.parsed)).at(-1);
  const all = stable.sort((a, b) => compare(a.parsed, b.parsed)).at(-1);
  return {
    sameMajor: (major) => {
      const hit = stable.filter((r) => r.parsed.major === major).at(-1);
      return hit ? hit.version.slice(1) : null;
    },
    latestLts: latestLts ? latestLts.version.slice(1) : null,
    latest: all ? all.version.slice(1) : null,
  };
}

// BepInEx 跟的不是上游最新，而是玩家实际装的那个包：
// 社区（r2modman / Thunderstore）安装的是 xiaoye97-BepInEx，它的最新版本就是玩家手上的版本。
// 编译目标高于它会让 BepInEx 打印 "targets a wrong version of BepInEx"。
async function bepinExVersions() {
  const community = await fetchJson('https://thunderstore.io/api/experimental/package/xiaoye97/BepInEx/');
  const upstream = await fetchJson('https://api.github.com/repos/BepInEx/BepInEx/releases/latest');
  return {
    community: String(community.latest?.version_number ?? ''),
    upstream: String(upstream.tag_name ?? '').replace(/^v/, ''),
  };
}

function readPackageJson() {
  return JSON.parse(readFileSync(resolve(SIDECAR_DIR, 'package.json'), 'utf8'));
}

// 本地参考源码的版本号与提交；目录不存在时返回 null（允许没部署参考源码）。
function readReferenceCheckout() {
  const pkgPath = resolve(REFERENCE_DIR, 'package.json');
  if (!existsSync(pkgPath)) return null;
  let version = null;
  try {
    version = JSON.parse(readFileSync(pkgPath, 'utf8')).version ?? null;
  } catch {
    version = null;
  }
  let commit = null;
  try {
    commit = execFileSync('git', ['-C', REFERENCE_DIR, 'rev-parse', '--short=7', 'HEAD'], {
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'ignore'],
    }).trim();
  } catch {
    commit = null;
  }
  return { version, commit };
}

// 上游 master 的提交与 package.json 版本；提示信息里用「版本 @ 短提交」标识同一份源码。
async function referenceUpstream() {
  const commit = await fetchJson(`https://api.github.com/repos/${REFERENCE_REPO}/commits/${REFERENCE_BRANCH}`);
  const sha = String(commit.sha ?? '');
  if (!sha) throw new Error('上游没有返回 commit');
  const contents = await fetchJson(
    `https://api.github.com/repos/${REFERENCE_REPO}/contents/package.json?ref=${sha}`,
  );
  const version = JSON.parse(Buffer.from(String(contents.content ?? ''), 'base64').toString('utf8')).version;
  return { sha, version: version ?? null };
}

function installedVersion(pkg) {
  const pkgJson = resolve(SIDECAR_DIR, 'node_modules', pkg, 'package.json');
  if (!existsSync(pkgJson)) return null;
  try {
    return JSON.parse(readFileSync(pkgJson, 'utf8')).version ?? null;
  } catch {
    return null;
  }
}

// 版本固定点写作 ${NAME:-x.y.z}，既可能是独立赋值，也可能内嵌在命令参数里，按这个记号读写。
function readPin(file, name) {
  const source = readFileSync(file, 'utf8');
  const match = new RegExp(`\\$\\{${name}:-([^}]+)\\}`).exec(source);
  return match ? match[1] : null;
}

function writePin(file, name, version) {
  const source = readFileSync(file, 'utf8');
  const next = source.replace(new RegExp(`\\$\\{${name}:-[^}]+\\}`), `\${${name}:-${version}}`);
  if (next === source) throw new Error(`没有改写 ${file} 里的 ${name}`);
  writeFileSync(file, next);
}

// 每条依赖的最终状态：up-to-date / update（同大版本可升）/ major（跨大版本）/ locked / unknown
function classify(entry, versions) {
  if (versions.error) return { ...entry, state: 'unknown', detail: versions.error };
  const current = parse(entry.current);
  if (!current) return { ...entry, state: 'unknown', detail: `无法解析版本 ${entry.current}` };
  const sameMajor = versions.sameMajor ? parse(versions.sameMajor) : null;
  const latest = versions.latest ? parse(versions.latest) : null;
  if (sameMajor && compare(sameMajor, current) > 0) {
    return { ...entry, state: 'update', target: versions.sameMajor };
  }
  if (latest && compare(latest, current) > 0) {
    return { ...entry, state: 'major', target: versions.latest };
  }
  return { ...entry, state: 'up-to-date', target: entry.current };
}

function rangePrefix(declared) {
  if (declared.startsWith('^')) return '^';
  if (declared.startsWith('~')) return '~';
  return '';
}

// 参考源码三种结果：没部署 / 与上游一致 / 上游有新提交（都不阻塞打包）。
function classifyReference(entry, upstream) {
  if (!entry.local) {
    return { ...entry, state: 'reference-missing', detail: `先执行：${REFERENCE_CLONE}` };
  }
  const local = `${entry.local.version ?? '未知版本'} @ ${entry.local.commit ?? '未知提交'}`;
  if (upstream.error) return { ...entry, state: 'unknown', detail: `${local}；${upstream.error}` };
  if (entry.local.commit && upstream.sha.startsWith(entry.local.commit)) {
    return { ...entry, state: 'up-to-date', detail: local };
  }
  const remote = `${upstream.version ?? '未知版本'} @ ${upstream.sha.slice(0, 7)}`;
  return { ...entry, state: 'reference-outdated', target: remote, detail: local };
}

async function collectEntries() {
  const pkg = readPackageJson();
  const entries = [];

  for (const [section, dev] of [
    ['dependencies', false],
    ['devDependencies', true],
  ]) {
    for (const [name, declared] of Object.entries(pkg[section] ?? {})) {
      const current = installedVersion(name) ?? declared.replace(/^[\^~]/, '');
      entries.push({
        kind: 'npm',
        label: name,
        name,
        dev,
        declared,
        current,
      });
    }
  }

  const nodePin = readPin(BUILD_SIDECAR, 'NODE_VERSION');
  if (nodePin) entries.push({ kind: 'node', label: 'Node 运行时（SEA）', pin: 'NODE_VERSION', file: BUILD_SIDECAR, current: nodePin });

  for (const pin of ['ESBUILD_VERSION', 'POSTJECT_VERSION']) {
    const value = readPin(BUILD_SIDECAR, pin);
    if (value) {
      const name = pin.replace('_VERSION', '').toLowerCase();
      entries.push({ kind: 'pin', label: name, name, pin, file: BUILD_SIDECAR, current: value });
    }
  }

  const bepinex = readPin(FETCH_DEPS, 'BEPINEX_VERSION');
  if (bepinex) entries.push({ kind: 'bepinex', label: 'BepInEx（编译期参考）', pin: 'BEPINEX_VERSION', file: FETCH_DEPS, current: bepinex, locked: true });

  const reference = readReferenceCheckout();
  entries.push({
    kind: 'reference',
    label: 'dsp-handcraft（蓝图参考源码）',
    current: reference?.version ?? '—',
    local: reference,
  });

  return entries;
}

async function resolveVersions(entries, nodeInfo) {
  const bepinexInfo = entries.some((e) => e.kind === 'bepinex') ? await bepinExVersions() : null;
  const referenceInfo = entries.some((e) => e.kind === 'reference')
    ? await referenceUpstream().catch((error) => ({ error: error.message }))
    : null;
  const results = [];
  for (const entry of entries) {
    try {
      if (entry.kind === 'reference') {
        results.push(classifyReference(entry, referenceInfo));
      } else if (entry.kind === 'node') {
        const current = parse(entry.current);
        results.push(
          classify(entry, {
            sameMajor: nodeInfo.sameMajor(current.major),
            latest: nodeInfo.latest,
          }),
        );
      } else if (entry.kind === 'bepinex') {
        const current = parse(entry.current);
        const community = parse(bepinexInfo.community);
        const detail = `社区 ${bepinexInfo.community} / 上游 ${bepinexInfo.upstream}`;
        if (!community) {
          results.push({ ...entry, state: 'unknown', detail: `读不到社区版本（${detail}）` });
        } else if (compare(current, community) > 0) {
          // 编译目标高于玩家手上的版本 → 会刷版本警告，必须降回社区版本。
          results.push({ ...entry, state: 'update', target: bepinexInfo.community, detail });
        } else if (compare(current, community) < 0) {
          // 社区已升到新版：编译到旧版仍然安全，跟不跟由人决定。
          results.push({ ...entry, state: 'minor-notice', target: bepinexInfo.community, detail });
        } else {
          results.push({ ...entry, state: 'up-to-date', detail });
        }
      } else {
        results.push(classify(entry, await npmVersions(entry.name, entry.current)));
      }
    } catch (error) {
      results.push({ ...entry, state: 'unknown', detail: error.message });
    }
  }
  return results;
}

function printResults(results, nodeInfo) {
  const statusText = {
    'up-to-date': '最新',
    update: '可更新',
    major: '仅大版本可用',
    'minor-notice': '社区更新',
    'reference-outdated': '上游更新',
    'reference-missing': '未部署',
    unknown: '未知',
  };
  const width = Math.max(...results.map((r) => r.label.length), 6);
  for (const r of results) {
    const target = ['update', 'major', 'minor-notice', 'reference-outdated'].includes(r.state) ? `→ ${r.target}` : '';
    const detail = r.detail ? `（${r.detail}）` : '';
    console.log(`  ${r.label.padEnd(width)}  ${r.current.padEnd(12)} ${statusText[r.state].padEnd(6)} ${target}${detail}`);
  }
  if (nodeInfo?.latestLts) {
    console.log(`\n  Node LTS 最新：${nodeInfo.latestLts}（当前线之外的升级需要人工决定）`);
  }
}

async function applyUpdates(results) {
  const applied = [];
  const skipped = [];
  const manual = [];
  for (const r of results) {
    const isMajor = r.state === 'major';
    if (r.state === 'locked' || r.state === 'up-to-date' || r.state === 'unknown') continue;
    if (r.kind === 'reference') {
      // 参考源码是独立 git 仓库，更新由人执行 `git pull`，脚本不代劳。
      if (r.state === 'reference-outdated') manual.push(r);
      continue;
    }
    if (isMajor && !allowMajor) {
      skipped.push(r);
      continue;
    }
    if (r.kind === 'npm') {
      const version = `${rangePrefix(r.declared)}${r.target.replace(/^v/, '')}`;
      execFileSync('npm', ['install', ...(r.dev ? ['--save-dev'] : []), `${r.name}@${version}`], {
        cwd: SIDECAR_DIR,
        stdio: 'inherit',
        env: process.env,
      });
    } else {
      writePin(r.file, r.pin, r.target.replace(/^v/, ''));
    }
    applied.push({ ...r, applied: r.target.replace(/^v/, '') });
  }
  return { applied, skipped, manual };
}

const entries = await collectEntries();
const nodeInfo = entries.some((e) => e.kind === 'node') ? await nodeVersions() : null;
const results = await resolveVersions(entries, nodeInfo);

console.log('依赖版本：');
printResults(results, nodeInfo);

const pending = results.filter((r) => r.state === 'update');
const majorOnly = results.filter((r) => r.state === 'major');

if (doUpdate) {
  const { applied, skipped, manual } = await applyUpdates(results);
  if (applied.length > 0) {
    console.log('\n已更新：');
    for (const r of applied) console.log(`  ${r.label} ${r.current} → ${r.applied}`);
    console.log('\n更新后必须重跑测试：cd sidecar && npm run typecheck && npm test');
  } else {
    console.log('\n没有可自动更新的依赖。');
  }
  if (skipped.length > 0) {
    console.log('\n跨大版本、需要人工决定：');
    for (const r of skipped) console.log(`  ${r.label} ${r.current} → ${r.target}（确认后用 --major 更新）`);
  }
  if (manual.length > 0) {
    console.log('\n参考源码需要手动更新（独立仓库，不随打包变化）：');
    for (const r of manual) console.log(`  git -C dsp-handcraft pull --ff-only   # ${r.label} ${r.current} → ${r.target}`);
  }
  process.exit(0);
}

if (majorOnly.length > 0) {
  console.log('\n跨大版本、需要人工决定（不阻塞打包）：');
  for (const r of majorOnly) console.log(`  ${r.label} ${r.current} → ${r.target}`);
}

const unverified = results.filter((r) => r.state === 'unknown');
if (unverified.length > 0) {
  console.log('\n以下依赖没能核对（网络或上游接口问题），打包前请确认：');
  for (const r of unverified) console.log(`  ${r.label} ${r.current}（${r.detail}）`);
}

const notices = results.filter((r) => r.state === 'minor-notice');
if (notices.length > 0) {
  console.log('\n社区已发新版本、可选跟进（不阻塞打包，跟了要实机验证）：');
  for (const r of notices) console.log(`  ${r.label} ${r.current} → ${r.target}`);
}

const referenceNotices = results.filter((r) => r.state === 'reference-outdated' || r.state === 'reference-missing');
if (referenceNotices.length > 0) {
  console.log('\n参考源码（只读参考，不阻塞打包）：');
  for (const r of referenceNotices) {
    const target = r.state === 'reference-outdated' ? ` → ${r.target}` : '';
    console.log(`  ${r.label} ${r.current}${target}`);
    if (r.state === 'reference-outdated') console.log('  更新：git -C dsp-handcraft pull --ff-only');
  }
}

if (pending.length > 0) {
  console.log('\n有依赖不是最新：node scripts/check-deps.mjs --update');
  process.exit(1);
}
console.log('\n全部依赖已是最新。');
