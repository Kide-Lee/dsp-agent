/**
 * 蓝图目录的受限虚拟文件系统。
 *
 * 模型只看到以 `/` 为根的虚拟路径；真实根永远是游戏的
 * `Documents/Dyson Sphere Program/Blueprint`。这样既能提供 ls/cd/pwd/cat/rm/mkdir
 * 的操作感，又不会把宿主文件系统暴露给模型。
 *
 * 安全约定（与 AGENTS.md 的“不暴露 shell/任意文件系统”一致）：
 *  - 拒绝 `..`、盘符、NUL、绝对系统路径与符号链接逃逸；
 *  - 所有真实路径必须落在 root 内（Windows 大小写不敏感）；
 *  - 删除是永久删除（用户明确选择），但仍要求 confirm；
 *  - 只由 blueprint_fs / blueprint_save / blueprint_generate 调用，不注册原始 fs。
 */

import {
  existsSync,
  lstatSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  realpathSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve, sep } from 'node:path';

export const DEFAULT_BLUEPRINT_SUBDIR = 'IcarusII';

export class BlueprintPathError extends Error {
  readonly code: string;
  constructor(code: string, message: string) {
    super(message);
    this.code = code;
  }
}

export interface VirtualPath {
  /** 以 / 开头的虚拟路径，例如 `/IcarusII/a.txt`。 */
  virtual: string;
  /** 相对蓝图根的路径，例如 `IcarusII/a.txt`；根为 ''。 */
  relPath: string;
  /** 真实绝对路径。 */
  real: string;
}

export interface BlueprintEntry {
  name: string;
  type: 'file' | 'directory' | 'symlink' | 'other';
  size: number;
  mtimeMs: number;
  relPath: string;
}

export interface ListOptions {
  recursive?: boolean;
  limit?: number;
  includeTrash?: boolean;
}

export interface BlueprintWorkspaceOptions {
  root?: string;
  defaultSubdir?: string;
}

function isWindows(): boolean {
  return process.platform === 'win32';
}

/** 蓝图根目录；与旧 `resolveBlueprintDir()` 同一口径。 */
export function resolveBlueprintRoot(): string {
  const override = process.env['ICARUS_BLUEPRINT_DIR'];
  if (override && override.trim()) return resolve(override.trim());
  const home = process.env['USERPROFILE'] || homedir();
  return resolve(join(home, 'Documents', 'Dyson Sphere Program', 'Blueprint'));
}

function sanitizeSegment(segment: string): string {
  const cleaned = segment
    .replace(/[<>:"/\\|?*\x00-\x1f]/g, ' ')
    .replace(/\s+/g, ' ')
    .replace(/^[. ]+|[. ]+$/g, '')
    .slice(0, 120);
  if (!cleaned || cleaned === '.' || cleaned === '..') {
    throw new BlueprintPathError('invalid_path', `非法路径片段：${segment}`);
  }
  return cleaned;
}

export class BlueprintWorkspace {
  readonly root: string;
  readonly defaultSubdir: string;
  private cwd = '/';

  constructor(options: BlueprintWorkspaceOptions = {}) {
    this.root = resolve(options.root ?? resolveBlueprintRoot());
    this.defaultSubdir = options.defaultSubdir ?? DEFAULT_BLUEPRINT_SUBDIR;
    if (!existsSync(this.root)) {
      try {
        mkdirSync(this.root, { recursive: true });
      } catch {
        // 目录不可创建时，后续操作会返回明确错误；构造本身不抛。
      }
    }
  }

  get defaultDir(): string {
    return `/${this.defaultSubdir}`;
  }

  get currentDir(): string {
    return this.cwd;
  }

  resetCwd(): void {
    this.cwd = '/';
  }

  setCwd(input: string): string {
    const target = this.resolveVirtual(input, this.cwd);
    if (!existsSync(target.real) || !statSync(target.real).isDirectory()) {
      throw new BlueprintPathError('not_a_directory', `不是目录：${target.virtual}`);
    }
    this.cwd = target.virtual;
    return this.cwd;
  }

  /**
   * 把模型给的路径解析成虚拟路径 + 真实路径。
   * `input` 为空 / '.' 时使用 `base`（默认当前 cwd）。
   */
  resolveVirtual(input: string | undefined, base: string = this.cwd): VirtualPath {
    const raw = (input ?? '').trim().replace(/\\/g, '/');
    if (raw.includes('\0') || /^[a-zA-Z]:/.test(raw) || raw.startsWith('//')) {
      throw new BlueprintPathError('invalid_path', `不允许的路径：${input ?? ''}`);
    }

    const baseParts = base.split('/').filter(Boolean);
    const parts = raw.startsWith('/') ? [] : [...baseParts];
    for (const segment of raw.split('/')) {
      if (!segment || segment === '.') continue;
      if (segment === '..') {
        if (parts.length === 0) {
          throw new BlueprintPathError('path_escape', '路径不能超出蓝图目录');
        }
        parts.pop();
        continue;
      }
      parts.push(sanitizeSegment(segment));
    }

    const relPath = parts.join('/');
    const real = resolve(this.root, ...parts);
    if (!this.isInside(real)) {
      throw new BlueprintPathError('path_escape', '路径不能超出蓝图目录');
    }
    this.assertNoSymlink(real);
    return {
      virtual: `/${relPath}`,
      relPath,
      real,
    };
  }

  /** 目标目录（用于保存）；默认 `IcarusII/`，不存在则创建。 */
  resolveTargetDir(input?: string): VirtualPath {
    const target = this.resolveVirtual(input && input.trim() ? input : this.defaultDir, '/');
    if (existsSync(target.real) && !statSync(target.real).isDirectory()) {
      throw new BlueprintPathError('not_a_directory', `不是目录：${target.virtual}`);
    }
    mkdirSync(target.real, { recursive: true });
    return target;
  }

  /** 在目标目录里找一个不冲突的 `.txt` 文件名。 */
  uniqueFileName(dir: VirtualPath, requested: string): { name: string; virtual: VirtualPath } {
    const base = sanitizeSegment(requested.replace(/\.txt$/i, '')) || 'blueprint';
    for (let index = 1; index < 50; index += 1) {
      const name = index === 1 ? `${base}.txt` : `${base} (${index}).txt`;
      const target = this.resolveVirtual(`${dir.relPath}/${name}`, '/');
      if (!existsSync(target.real)) return { name, virtual: target };
    }
    throw new BlueprintPathError('too_many_collisions', `同名文件过多：${base}`);
  }

  list(input?: string, options: ListOptions = {}): { path: VirtualPath; entries: BlueprintEntry[]; truncated: boolean } {
    const path = this.resolveVirtual(input);
    if (!existsSync(path.real)) {
      throw new BlueprintPathError('not_found', `目录不存在：${path.virtual}`);
    }
    const stat = lstatSync(path.real);
    if (stat.isSymbolicLink()) {
      throw new BlueprintPathError('symlink_not_allowed', `不允许符号链接：${path.virtual}`);
    }
    if (!stat.isDirectory()) return { path, entries: [this.entryFor(path)], truncated: false };

    const limit = Math.max(1, Math.min(options.limit ?? 200, 1000));
    const entries: BlueprintEntry[] = [];
    let truncated = false;
    const includeTrash = options.includeTrash === true || path.virtual.startsWith('/.trash');
    const maxDepth = 8;

    const walk = (dir: VirtualPath, depth: number): void => {
      let dirents;
      try {
        dirents = readdirSync(dir.real, { withFileTypes: true });
      } catch {
        return;
      }
      dirents.sort((a, b) => {
        if (a.isDirectory() !== b.isDirectory()) return a.isDirectory() ? -1 : 1;
        return a.name.localeCompare(b.name);
      });
      for (const dirent of dirents) {
        if (entries.length >= limit) {
          truncated = true;
          return;
        }
        if (!includeTrash && dirent.name === '.trash') continue;
        let child: VirtualPath;
        try {
          child = this.resolveVirtual(`${dir.relPath}/${dirent.name}`, '/');
        } catch {
          continue;
        }
        const entry = this.entryFor(child);
        entries.push(entry);
        if (options.recursive && entry.type === 'directory' && depth < maxDepth) {
          walk(child, depth + 1);
        }
      }
    };

    walk(path, 0);
    return { path, entries, truncated };
  }

  read(input: string, maxBytes = 2 * 1024 * 1024): { path: VirtualPath; text: string; size: number; mtimeMs: number } {
    const path = this.resolveVirtual(input);
    if (!existsSync(path.real)) {
      throw new BlueprintPathError('not_found', `文件不存在：${path.virtual}`);
    }
    const stat = lstatSync(path.real);
    if (stat.isSymbolicLink()) {
      throw new BlueprintPathError('symlink_not_allowed', `不允许符号链接：${path.virtual}`);
    }
    if (!stat.isFile()) throw new BlueprintPathError('not_a_file', `不是文件：${path.virtual}`);
    if (stat.size > maxBytes) {
      throw new BlueprintPathError('too_large', `文件过大：${path.virtual}（${stat.size} bytes）`);
    }
    return {
      path,
      text: readFileSync(path.real, 'utf8'),
      size: stat.size,
      mtimeMs: stat.mtimeMs,
    };
  }

  mkdir(input: string, recursive = true): VirtualPath {
    const path = this.resolveVirtual(input);
    if (existsSync(path.real)) {
      if (!statSync(path.real).isDirectory()) {
        throw new BlueprintPathError('not_a_directory', `已存在同名文件：${path.virtual}`);
      }
      return path;
    }
    mkdirSync(path.real, { recursive });
    return path;
  }

  write(
    input: string,
    content: string,
    options: { overwrite?: boolean; parents?: boolean } = {},
  ): { path: VirtualPath; size: number } {
    const path = this.resolveVirtual(input);
    if (existsSync(path.real) && options.overwrite !== true) {
      throw new BlueprintPathError('exists', `文件已存在：${path.virtual}（需要 overwrite=true）`);
    }
    if (options.parents !== false) mkdirSync(dirname(path.real), { recursive: true });
    const tmp = `${path.real}.tmp-${process.pid}-${Date.now()}`;
    writeFileSync(tmp, content, 'utf8');
    renameSync(tmp, path.real);
    return { path, size: Buffer.byteLength(content, 'utf8') };
  }

  remove(input: string): VirtualPath {
    const path = this.resolveVirtual(input);
    if (path.virtual === '/') throw new BlueprintPathError('root_protected', '不能删除蓝图根目录');
    if (!existsSync(path.real)) throw new BlueprintPathError('not_found', `文件不存在：${path.virtual}`);
    const stat = lstatSync(path.real);
    if (stat.isSymbolicLink()) throw new BlueprintPathError('symlink_not_allowed', '不允许删除符号链接');
    if (stat.isDirectory()) throw new BlueprintPathError('is_directory', '目录请用 rmdir');
    rmSync(path.real, { force: true });
    return path;
  }

  removeDir(input: string, recursive = false): VirtualPath {
    const path = this.resolveVirtual(input);
    if (path.virtual === '/') throw new BlueprintPathError('root_protected', '不能删除蓝图根目录');
    if (!existsSync(path.real)) throw new BlueprintPathError('not_found', `目录不存在：${path.virtual}`);
    const stat = lstatSync(path.real);
    if (stat.isSymbolicLink()) throw new BlueprintPathError('symlink_not_allowed', '不允许删除符号链接');
    if (!stat.isDirectory()) throw new BlueprintPathError('not_a_directory', `不是目录：${path.virtual}`);
    if (!recursive && readdirSync(path.real).length > 0) {
      throw new BlueprintPathError('not_empty', `目录非空：${path.virtual}（需要 recursive=true）`);
    }
    rmSync(path.real, { recursive: true, force: true });
    return path;
  }

  private entryFor(path: VirtualPath): BlueprintEntry {
    let stat;
    try {
      stat = lstatSync(path.real);
    } catch {
      return { name: path.relPath.split('/').pop() ?? path.relPath, type: 'other', size: 0, mtimeMs: 0, relPath: path.relPath };
    }
    return {
      name: path.relPath.split('/').pop() ?? path.relPath,
      type: stat.isSymbolicLink() ? 'symlink' : stat.isDirectory() ? 'directory' : stat.isFile() ? 'file' : 'other',
      size: stat.isFile() ? stat.size : 0,
      mtimeMs: stat.mtimeMs,
      relPath: path.relPath,
    };
  }

  private isInside(real: string): boolean {
    const root = isWindows() ? this.root.toLowerCase() : this.root;
    const target = isWindows() ? real.toLowerCase() : real;
    return target === root || target.startsWith(root + sep);
  }

  /** 拒绝路径链上任何符号链接（含已存在的父目录），防止逃逸。 */
  private assertNoSymlink(real: string): void {
    const root = isWindows() ? this.root.toLowerCase() : this.root;
    const target = isWindows() ? real.toLowerCase() : real;
    if (!(target === root || target.startsWith(root + sep))) {
      throw new BlueprintPathError('path_escape', '路径不能超出蓝图目录');
    }
    const parts = target.slice(root.length).split(sep).filter(Boolean);
    let current = this.root;
    for (const part of parts) {
      current = join(current, part);
      if (!existsSync(current)) break;
      try {
        if (lstatSync(current).isSymbolicLink()) {
          throw new BlueprintPathError('symlink_not_allowed', '不允许符号链接');
        }
        realpathSync(current);
      } catch (err) {
        if (err instanceof BlueprintPathError) throw err;
        break;
      }
    }
  }
}
