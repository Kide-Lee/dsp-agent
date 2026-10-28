#!/usr/bin/env python3
"""离线清点 DSPGame 场景里某个 UI 子树的组件（不启动 Unity）。

场景里 MonoBehaviour 的类名被剥离，这里按序列化字段签名归类；
映射表见 COMPONENT_RULES，改规则即可支持新组件。

用法:
    python scene-ui-inventory.py                       # In Game 子树：频次 + 一级结构
    python scene-ui-inventory.py --root "Function Panel" --depth 2
    python scene-ui-inventory.py --root "Windows" --stats
    python scene-ui-inventory.py --scene path/to/X.unity --root "In Game"

依赖: UnityPy（安装命令见 references/scene-inventory.md）
"""
import argparse
import sys
from collections import Counter

import UnityPy

DEFAULT_SCENE = "dsp-game/Assets/Scenes/DSPGame.unity"

# 字段签名 → 组件名；按顺序匹配，先命中先用
COMPONENT_RULES = [
    (("m_HandleRect",), "Slider"),
    (("m_TextComponent", "m_Placeholder"), "InputField"),
    (("m_Delegates",), "EventTrigger"),
    (("m_ShowMaskGraphic",), "Mask"),
    (("m_EffectColor",), "Outline"),
    (("m_Texture", "m_UVRect"), "RawImage"),
    (("m_Content", "m_Viewport"), "ScrollRect"),
    (("m_DropDownList",), "UIComboBox"),
    (("m_Sprite", "m_FillAmount"), "Image"),
    (("m_FontData",), "Text"),
    (("m_Navigation", "m_OnClick"), "Button"),
    (("button", "input", "eventReceiver"), "UIButton"),
    (("stringKey", "translation", "posApplyTrans"), "Localizer"),
    (("inputField", "overText", "iconSelectBtn"), "UIInputField"),
]

SKIP_FIELDS = {
    "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset",
    "m_GameObject", "m_Enabled", "m_EditorHideFlags", "m_Script", "m_Name", "m_EditorClassIdentifier",
}

WIDGETS = ("UIButton", "Button", "Image", "Text", "Localizer", "InputField",
           "Slider", "EventTrigger", "Mask", "RawImage", "ScrollRect", "UIComboBox")


def classify(fields):
    fset = set(fields)
    for need, name in COMPONENT_RULES:
        if all(n in fset for n in need):
            return name
    return None


class Scene:
    def __init__(self, path):
        env = UnityPy.load(path)
        objs = {o.path_id: o for o in env.objects}

        self.go = {}            # go pid -> (name, [labels], [(label, fields)])
        for pid, o in objs.items():
            if o.type.name != "GameObject":
                continue
            data = o.read()
            labels, sigs = [], []
            for comp in data.m_Component:
                co = objs.get(comp.component.path_id)
                if co is None:
                    continue
                if co.type.name == "MonoBehaviour":
                    fields = tuple(x.m_Name for x in co.serialized_type.node.m_Children)
                    label = classify(fields) or "?"
                    labels.append(label)
                    sigs.append((label, fields))
                else:
                    labels.append(co.type.name)
            self.go[pid] = (data.m_Name, labels, sigs)

        self.tchildren = {}     # transform pid -> [child transform pid]
        self.t2g = {}           # transform pid -> gameobject pid
        self.g2t = {}           # gameobject pid -> transform pid
        for pid, o in objs.items():
            if o.type.name not in ("Transform", "RectTransform"):
                continue
            t = o.read()
            self.tchildren[pid] = [c.path_id for c in t.m_Children]
            if t.m_GameObject:
                self.t2g[pid] = t.m_GameObject.path_id
                self.g2t[t.m_GameObject.path_id] = pid

    def children(self, go_pid):
        t = self.g2t.get(go_pid)
        return [self.t2g[c] for c in self.tchildren.get(t, []) if c in self.t2g]

    def find(self, name):
        return [pid for pid, (n, _l, _s) in self.go.items() if n == name]

    def subtree(self, root):
        out, stack = [], [root]
        while stack:
            pid = stack.pop()
            out.append(pid)
            stack.extend(self.children(pid))
        return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scene", default=DEFAULT_SCENE)
    ap.add_argument("--root", default="In Game")
    ap.add_argument("--depth", type=int, default=1, help="结构打印深度")
    ap.add_argument("--stats", action="store_true", help="只打频次与组合")
    args = ap.parse_args()

    scene = Scene(args.scene)
    roots = scene.find(args.root)
    if not roots:
        print("找不到节点：%s" % args.root, file=sys.stderr)
        return 2

    nodes = []
    for r in roots:
        nodes.extend(scene.subtree(r))

    counts, combos, unknown = Counter(), Counter(), Counter()
    for pid in nodes:
        _name, labels, sigs = scene.go[pid]
        counts.update(labels)
        for label, fields in sigs:
            if label == "?":
                unknown[fields] += 1
        widgets = tuple(sorted({x for x in labels if x in WIDGETS}))
        if widgets:
            combos[widgets] += 1

    print("root=%s  nodes=%d" % (args.root, len(nodes)))
    print("\n组件频次:")
    for name, n in counts.most_common(30):
        print("%6d  %s" % (n, name))

    print("\n节点组合 top 15:")
    for combo, n in combos.most_common(15):
        print("%6d  %s" % (n, " + ".join(combo)))

    if unknown:
        print("\n未归类签名 top 10:")
        for fields, n in unknown.most_common(10):
            core = [f for f in fields if f not in SKIP_FIELDS]
            print("%6d  %s" % (n, ", ".join(core[:12])))

    if not args.stats:
        print("\n结构（深度<=%d）:" % args.depth)

        def walk(pid, depth):
            name, labels, _sigs = scene.go[pid]
            print("%s%s  [%s]" % ("  " * depth, name, ", ".join(x for x in labels if x != "?")))
            if depth < args.depth:
                for child in scene.children(pid):
                    walk(child, depth + 1)

        for r in roots:
            walk(r, 0)
    return 0


if __name__ == "__main__":
    sys.exit(main())
