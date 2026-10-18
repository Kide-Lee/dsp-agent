#!/usr/bin/env python3
"""从游戏安装/提取出的 Unity 资源离线生成 kb-v1 JSON（M3 开发与离线测试用）。

产品路径是 mod 的 game_export_prototypes（运行时导出）；这个脚本用于：
  - 在没有游戏进程时开发/验证 kb_* 工具；
  - 生成全量 fixture，和运行时导出做结构/数值对照。

用法：
  PYTHONPATH=.scratch/pylib python3 tools/build-kb.py \
      --prototypes dsp-game/Assets/Resources/Prototypes \
      --locale-dir dsp-game/Locale/2052 \
      --tutorial-dir dsp-game/Wiki/Tutorial \
      --locale zh-CN --game-version 0.10.30.3100 \
      --out .scratch/kb/kb-zh.json

依赖：UnityPy（pip install UnityPy）。脚本只读文件，不执行游戏代码。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
# 本项目开发机上 UnityPy 装在 .scratch/pylib；没有就要求用户自己安装。
for extra in (ROOT / ".scratch" / "pylib",):
    if extra.exists() and str(extra) not in sys.path:
        sys.path.insert(0, str(extra))

try:
    import UnityPy  # type: ignore
except Exception as exc:  # pragma: no cover - 运行环境问题
    sys.stderr.write("需要 UnityPy：pip install UnityPy\n")
    raise SystemExit(2) from exc

LCID_TO_TUTORIAL = {
    "2052": "zh",
    "1033": "en",
    "1036": "fr",
    "1031": "de",
    "3082": "es",
    "1041": "ja",
    "1042": "ko",
    "1049": "ru",
}

LKEY = re.compile(r"\$LKEY_\d+;")
BRACE = re.compile(r"\{\d+(?::[^}]*)?\}")
RICH = re.compile(r"<[^>]+>")
ICON = re.compile(r"\\+[A-Za-z0-9_\-]+;")


def load_rows(path: Path) -> list[dict[str, Any]]:
    env = UnityPy.load(str(path))
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        rows = tree.get("dataArray")
        if isinstance(rows, list):
            return rows
    return []


def read_locale(path: Path) -> dict[str, str]:
    """UTF-16LE、Tab 分隔；取第一列作 key、最后一列作文本。"""
    if not path.exists():
        return {}
    raw = path.read_bytes()
    try:
        text = raw.decode("utf-16")
    except UnicodeDecodeError:
        text = raw.decode("utf-8-sig")
    out: dict[str, str] = {}
    for line in text.splitlines():
        if not line or "\t" not in line:
            continue
        parts = line.split("\t")
        key = parts[0].lstrip("\ufeff").strip()
        value = parts[-1]
        if key and key not in out:
            out[key] = value
    return out


def load_locale_dir(locale_dir: Path) -> dict[str, str]:
    merged: dict[str, str] = {}
    # prototype 优先（物品/科技/指引名），base 补 UI/顾问/里程碑/成就，dictionary 补术语。
    for name in ("prototype.txt", "base.txt", "dictionary.txt", "keys.txt"):
        for key, value in read_locale(locale_dir / name).items():
            merged.setdefault(key, value)
    return merged


def translate(table: dict[str, str], key: Any) -> str:
    if not isinstance(key, str) or not key:
        return ""
    return table.get(key, key)


def normalize(text: Any) -> tuple[str, bool, bool]:
    """返回 (清洗后文本, 是否含模板参数, 是否含未解析按键)。"""
    if not isinstance(text, str):
        return "", False, False
    raw = text
    unresolved = bool(LKEY.search(raw))
    templated = bool(BRACE.search(raw))
    s = raw.replace("\\r\\n", "\n").replace("\\n", "\n")
    s = s.replace("\r\n", "\n").replace("\r", "\n")
    s = s.replace("\u00a0", " ").replace("\u2009", " ").replace("\u2005", " ").replace("\u200b", "")
    s = RICH.sub("", s)
    s = ICON.sub("", s)
    s = LKEY.sub("[按键]", s)
    s = re.sub(r"<[^>]*$", "", s)  # 截断的富文本标签
    s = re.sub(r"[ \t]+", " ", s)
    s = re.sub(r"\n{3,}", "\n\n", s)
    return s.strip(), templated, unresolved


def clean(value: Any) -> str:
    return normalize(value)[0]


def text_entry(
    kind: str,
    ident: int | str,
    title: str,
    body: str,
    *,
    source: str,
    spoiler: str = "low",
    phase: int = 0,
    extra: dict[str, Any] | None = None,
) -> dict[str, Any] | None:
    body2, templated, unresolved = normalize(body)
    title2, title_templated, title_unresolved = normalize(title)
    if not body2 and not title2:
        return None
    entry: dict[str, Any] = {
        "kind": kind,
        "id": ident,
        "title": title2,
        "body": body2,
        "phase": phase,
        "spoiler": spoiler,
        "source": source,
        "templated": templated or title_templated,
        "unresolvedKeys": unresolved or title_unresolved,
    }
    if extra:
        entry["extra"] = extra
    return entry


def parse_tutorial(path: Path, title: str) -> str:
    if not path.exists():
        return ""
    try:
        raw = path.read_text(encoding="utf-8-sig")
    except UnicodeDecodeError:
        raw = path.read_text(encoding="utf-8", errors="replace")
    lines = [line for line in raw.splitlines() if not line.lstrip().startswith("{$")]
    body = "\n".join(lines).strip()
    if title and body.startswith(title):
        body = body[len(title) :].lstrip("\n ")
    body = re.sub(r"\n{3,}", "\n\n", body)
    return body


def build_items(rows: list[dict[str, Any]], table: dict[str, str]) -> list[dict[str, Any]]:
    items: list[dict[str, Any]] = []
    for row in rows:
        item = {
            "id": int(row.get("ID", 0)),
            "sid": row.get("SID") or None,
            "name": clean(translate(table, row.get("Name"))),
            "description": clean(translate(table, row.get("Description"))) or None,
            "type": int(row.get("Type", 0)),
            "subId": int(row.get("SubID", 0)),
            "stackSize": int(row.get("StackSize", 0)),
            "grade": int(row.get("Grade", 0)),
            "isFluid": bool(row.get("IsFluid", False)),
            "isEntity": bool(row.get("IsEntity", False)),
            "canBuild": bool(row.get("CanBuild", False)),
            "buildInGas": bool(row.get("BuildInGas", False)),
            "gridIndex": int(row.get("GridIndex", 0)),
            "iconTag": row.get("IconTag") or None,
            "miningFrom": clean(translate(table, row.get("MiningFrom"))) or None,
            "produceFrom": clean(translate(table, row.get("ProduceFrom"))) or None,
            "modelIndex": int(row.get("ModelIndex", 0)),
            "modelCount": int(row.get("ModelCount", 0)),
            "unlockKey": int(row.get("UnlockKey", 0)),
            "preTechOverride": int(row.get("PreTechOverride", 0)),
            "productive": bool(row.get("Productive", False)),
        }
        items.append({key: value for key, value in item.items() if value is not None})
    return items


def build_recipes(rows: list[dict[str, Any]], table: dict[str, str]) -> list[dict[str, Any]]:
    recipes: list[dict[str, Any]] = []
    for row in rows:
        ids = row.get("Items") or []
        counts = row.get("ItemCounts") or []
        results = row.get("Results") or []
        result_counts = row.get("ResultCounts") or []
        time_spend = int(row.get("TimeSpend", 0) or 0)
        recipe = {
            "id": int(row.get("ID", 0)),
            "sid": row.get("SID") or None,
            "name": clean(translate(table, row.get("Name"))),
            "description": clean(translate(table, row.get("Description"))) or None,
            "type": int(row.get("Type", 0)),
            "handcraft": bool(row.get("Handcraft", False)),
            "explicit": bool(row.get("Explicit", False)),
            "timeSpend": time_spend,
            "timeSeconds": round(time_spend / 60.0, 4),
            "inputs": [
                {"id": int(ids[i]), "count": int(counts[i]) if i < len(counts) else 1}
                for i in range(len(ids))
            ],
            "outputs": [
                {"id": int(results[i]), "count": int(result_counts[i]) if i < len(result_counts) else 1}
                for i in range(len(results))
            ],
            "nonProductive": bool(row.get("NonProductive", False)),
            "iconTag": row.get("IconTag") or None,
        }
        recipes.append({key: value for key, value in recipe.items() if value is not None})
    return recipes


def attach_main_recipes(items: list[dict[str, Any]], recipes: list[dict[str, Any]]) -> None:
    by_output: dict[int, list[tuple[int, int]]] = {}
    for recipe in recipes:
        for index, output in enumerate(recipe["outputs"]):
            by_output.setdefault(int(output["id"]), []).append((index, int(recipe["id"])))
    for item in items:
        candidates = sorted(by_output.get(int(item["id"]), []), key=lambda pair: (pair[0], pair[1]))
        if candidates:
            item["mainRecipeId"] = candidates[0][1]
        hand = [pair for pair in candidates if any(r["id"] == pair[1] and r.get("handcraft") for r in recipes)]
        if hand:
            item["handRecipeId"] = sorted(hand, key=lambda pair: (pair[0], pair[1]))[0][1]


def build_techs(rows: list[dict[str, Any]], table: dict[str, str]) -> list[dict[str, Any]]:
    techs: list[dict[str, Any]] = []
    for row in rows:
        tech = {
            "id": int(row.get("ID", 0)),
            "sid": row.get("SID") or None,
            "name": clean(translate(table, row.get("Name"))),
            "description": clean(translate(table, row.get("Desc"))) or None,
            "conclusion": clean(translate(table, row.get("Conclusion"))) or None,
            "level": int(row.get("Level", 0)),
            "maxLevel": int(row.get("MaxLevel", 0)),
            "preTechs": [int(v) for v in (row.get("PreTechs") or [])],
            "preTechsImplicit": [int(v) for v in (row.get("PreTechsImplicit") or [])],
            "items": [int(v) for v in (row.get("Items") or [])],
            "itemPoints": [int(v) for v in (row.get("ItemPoints") or [])],
            "hashNeeded": int(row.get("HashNeeded", 0)),
            "unlockRecipes": [int(v) for v in (row.get("UnlockRecipes") or [])],
            "unlockFunctions": [int(v) for v in (row.get("UnlockFunctions") or [])],
            "unlockValues": [float(v) for v in (row.get("UnlockValues") or [])],
            "published": bool(row.get("Published", False)),
            "isHiddenTech": bool(row.get("IsHiddenTech", False)),
            "isObsolete": bool(row.get("IsObsolete", False)),
            "iconTag": row.get("IconTag") or None,
        }
        techs.append({key: value for key, value in tech.items() if value is not None})
    return techs


def build_models(rows: list[dict[str, Any]], table: dict[str, str]) -> list[dict[str, Any]]:
    models: list[dict[str, Any]] = []
    for row in rows:
        override = clean(translate(table, row.get("OverrideName")))
        sid = clean(translate(table, row.get("SID")))
        name = override or sid or f"模型 {row.get('ID', 0)}"
        models.append(
            {
                "id": int(row.get("ID", 0)),
                "sid": row.get("SID") or None,
                "name": name,
                "objectType": int(row.get("ObjectType", 0)),
                "prefabPath": row.get("PrefabPath") or None,
            }
        )
    return models


def build_veins(rows: list[dict[str, Any]], table: dict[str, str]) -> list[dict[str, Any]]:
    veins: list[dict[str, Any]] = []
    for row in rows:
        veins.append(
            {
                "id": int(row.get("ID", 0)),
                "sid": row.get("SID") or None,
                "name": clean(translate(table, row.get("Name"))),
                "description": clean(translate(table, row.get("Description"))) or None,
                "miningItem": int(row.get("MiningItem", 0)),
                "miningTime": int(row.get("MiningTime", 0)),
                "iconTag": row.get("IconTag") or None,
            }
        )
    return veins


def build_signals(rows: list[dict[str, Any]], table: dict[str, str]) -> list[dict[str, Any]]:
    return [
        {
            "id": int(row.get("ID", 0)),
            "sid": row.get("SID") or None,
            "name": clean(translate(table, row.get("Name"))),
            "iconTag": row.get("IconTag") or None,
        }
        for row in rows
    ]


def build_texts(
    rows: dict[str, list[dict[str, Any]]],
    table: dict[str, str],
    tutorial_dir: Path,
    tutorial_suffix: str,
) -> list[dict[str, Any]]:
    texts: list[dict[str, Any]] = []

    for row in rows.get("AdvisorTipProtoSet", []):
        body = translate(table, row.get("Script"))
        entry = text_entry(
            "tip",
            int(row.get("ID", 0)),
            f"顾问提示 {row.get('ID', 0)}",
            body,
            source="base.txt",
            spoiler="low",
            extra={"priority": row.get("Priority", 0), "voice": row.get("Voice") or None},
        )
        if entry:
            texts.append(entry)

    for row in rows.get("TutorialProtoSet", []):
        title = translate(table, row.get("Name"))
        layout = row.get("LayoutFileName") or ""
        body = parse_tutorial(tutorial_dir / f"{layout}-{tutorial_suffix}.txt", title) if layout else ""
        entry = text_entry(
            "tutorial",
            int(row.get("ID", 0)),
            title,
            body,
            source=f"Wiki/Tutorial/{layout}-{tutorial_suffix}.txt",
            spoiler="story" if int(row.get("ID", 0)) <= 1 else "low",
            extra={"layoutFile": layout},
        )
        if entry:
            texts.append(entry)

    for row in rows.get("MilestoneProtoSet", []):
        name = translate(table, row.get("Name"))
        desc = translate(table, row.get("DefaultDesc"))
        condition = translate(table, row.get("ConditionString"))
        body = "\n".join(part for part in (desc, condition) if part)
        position = row.get("Position") or {}
        entry = text_entry(
            "milestone",
            int(row.get("ID", 0)),
            name,
            body,
            source="base.txt",
            spoiler="low",
            extra={
                "position": [position.get("x"), position.get("y")],
                "journalPatternId": row.get("JournalPatternId", 0),
            },
        )
        if entry:
            texts.append(entry)

    for row in rows.get("GoalProtoSet", []):
        name = translate(table, row.get("Name"))
        raw_tooltip = row.get("TooltipText") or ""
        tooltip = table.get(raw_tooltip, "") if raw_tooltip in table else ""
        tooltip = clean(tooltip) or clean(name)
        entry = text_entry(
            "goal",
            int(row.get("ID", 0)),
            name,
            tooltip,
            source="prototype.txt",
            spoiler="low",
            phase=int(row.get("Level", 0) or 0),
            extra={
                "level": row.get("Level", 0),
                "parentId": row.get("ParentId", 0),
                "children": row.get("Childs") or [],
                "determinator": row.get("DeterminatorName") or None,
            },
        )
        if entry:
            texts.append(entry)

    for row in rows.get("PromptProtoSet", []):
        body = translate(table, row.get("Text"))
        title = normalize(body)[0][:24] or f"控制提示 {row.get('ID', 0)}"
        entry = text_entry(
            "prompt",
            int(row.get("ID", 0)),
            title,
            body,
            source="base.txt",
            spoiler="none",
        )
        if entry:
            texts.append(entry)

    for row in rows.get("AchievementProtoSet", []):
        entry = text_entry(
            "achievement",
            int(row.get("ID", 0)),
            translate(table, row.get("Name")),
            translate(table, row.get("Description")),
            source="base.txt",
            spoiler="low",
            extra={"targetValue": row.get("TargetValue", 0)},
        )
        if entry:
            texts.append(entry)

    for row in rows.get("JournalPatternProtoSet", []):
        entry = text_entry(
            "journal",
            int(row.get("ID", 0)),
            f"里程碑日志 {row.get('ID', 0)}",
            translate(table, row.get("JournalDesc")),
            source="base.txt",
            spoiler="low",
            extra={"dataTypes": row.get("DataTypes") or []},
        )
        if entry:
            texts.append(entry)

    # 术语表：dictionary.txt 的 key 形如「戴森球(字典)」，value 是官方译名。
    for key, value in table.items():
        if not key.endswith("(字典)"):
            continue
        term = normalize(value)[0]
        if not term:
            continue
        entry = text_entry(
            "glossary",
            key,
            term,
            term,
            source="dictionary.txt",
            spoiler="none",
        )
        if entry:
            texts.append(entry)

    # 同 kind + id 去重（后出现的保留；通常不会发生）。
    dedup: dict[tuple[str, str], dict[str, Any]] = {}
    for entry in texts:
        dedup[(str(entry["kind"]), str(entry["id"]))] = entry
    return list(dedup.values())


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--prototypes", required=True, type=Path)
    parser.add_argument("--locale-dir", required=True, type=Path)
    parser.add_argument("--tutorial-dir", type=Path, default=None)
    parser.add_argument("--locale", default="zh-CN")
    parser.add_argument("--lcid", default=None, help="默认从 --locale 推断，例如 zh-CN → 2052")
    parser.add_argument("--game-version", default="0.10.30.3100")
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--no-texts", action="store_true", help="只导出结构化原型（M3a）")
    args = parser.parse_args()

    lcid = args.lcid or {
        "zh-CN": "2052",
        "en-US": "1033",
        "fr-FR": "1036",
        "de-DE": "1031",
        "es-ES": "3082",
        "ja-JP": "1041",
        "ko-KR": "1042",
        "ru-RU": "1049",
    }.get(args.locale, "2052")
    table = load_locale_dir(args.locale_dir)

    rows: dict[str, list[dict[str, Any]]] = {}
    for name in (
        "ItemProtoSet",
        "RecipeProtoSet",
        "TechProtoSet",
        "ModelProtoSet",
        "VeinProtoSet",
        "SignalProtoSet",
        "AdvisorTipProtoSet",
        "TutorialProtoSet",
        "MilestoneProtoSet",
        "GoalProtoSet",
        "PromptProtoSet",
        "AchievementProtoSet",
        "JournalPatternProtoSet",
    ):
        path = args.prototypes / f"{name}.asset"
        rows[name] = load_rows(path) if path.exists() else []
        print(f"{name}: {len(rows[name])}", file=sys.stderr)

    items = build_items(rows["ItemProtoSet"], table)
    recipes = build_recipes(rows["RecipeProtoSet"], table)
    attach_main_recipes(items, recipes)

    data: dict[str, Any] = {
        "schemaVersion": 1,
        "gameVersion": args.game_version,
        "locale": args.locale,
        "builtAt": datetime.now(timezone.utc).isoformat(),
        "source": "offline-extract",
        "items": items,
        "recipes": recipes,
        "techs": build_techs(rows["TechProtoSet"], table),
        "models": build_models(rows["ModelProtoSet"], table),
        "veins": build_veins(rows["VeinProtoSet"], table),
        "signals": build_signals(rows["SignalProtoSet"], table),
        "texts": [] if args.no_texts else build_texts(
            rows,
            table,
            args.tutorial_dir or (args.prototypes.parent.parent.parent / "Wiki" / "Tutorial"),
            LCID_TO_TUTORIAL.get(lcid, "zh"),
        ),
    }

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
    print(
        f"wrote {args.out} ({args.out.stat().st_size} bytes): "
        f"{len(items)} items, {len(recipes)} recipes, {len(data['techs'])} techs, "
        f"{len(data['texts'])} texts",
        file=sys.stderr,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
