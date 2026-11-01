"""生成「待补资源清单」：人读文档 + 可直接填写的 TSV。

为什么需要：游戏内没有文本的实体（基础/起义卡效果、冲突奖励、空间效果、科技）只能外部补，
但补之前得先让人看见"到底缺什么、缺多少、键是什么"。

过滤规则：本地化里存在 "st/nd/rd" 这类碎片名（值只有「名」），不是真卡，排除。

输出：
  docs/待补资源清单.md      人读版，表格留空待填
  mods/ai/kb_todo_fill.tsv  可填版：键 / 中文名 / 类型 / 待填文本 / 来源
用法：python mods/tools/build_todo_doc.py
"""
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "mods", "tools"))
import build_name_map as B

DOC = os.path.join(ROOT, "docs", "待补资源清单.md")
TSV = os.path.join(ROOT, "mods", "ai", "kb_todo_fill.tsv")
TEXT_SLOTS = {"agent", "reveal", "text", "ability", "static", "text1", "text2"}
JUNK = {"名", "选择两个：", ""}


def effects():
    out = {}
    for line in open(os.path.join(ROOT, "mods", "ai", "effect_map.tsv"), encoding="utf-8"):
        k, _, v = line.rstrip("\n").partition("\t")
        out[k] = v
    return out


def main():
    zh = B.load_locale()
    eff = effects()
    cards = json.load(open(os.path.join(ROOT, "dump", "cards_runtime.json"), encoding="utf-8-sig"))
    img2guid, img2sets = {}, {}
    for c in cards:
        if c.get("image") and c.get("id"):
            img2guid.setdefault(c["image"], c["id"])
            img2sets.setdefault(c["image"], [str(x).lower() for x in (c.get("sets") or [])])

    def fam(img):
        s = img2sets.get(img, [])
        for k in ("uprising", "ix", "immortality", "tleilaxu", "baseset"):
            if any(k in x for x in s):
                return k
        return s[0] if s else "?"

    # A 卡牌
    title, hastext = {}, set()
    for k, v in zh.items():
        m = re.match(r"^card\.imperium\.([a-z0-9_]+)\.([a-z0-9_.]+)$", k)
        if not m:
            continue
        img, slot = m.group(1), m.group(2).split(".")[0]
        if slot == "title":
            title[img] = v.strip()
        elif slot in TEXT_SLOTS:
            hastext.add(img)
    cards_gap = sorted(
        [(title[i], img2guid.get(i, ""), fam(i), i) for i in title
         if i not in hastext and "name:" + title[i] not in eff],
        key=lambda x: (x[2], x[0]))

    # B 冲突
    conf = {}
    for k, v in zh.items():
        m = re.match(r"^card\.conflict\.([a-z0-9_]+)$", k)
        if m:
            conf[m.group(1)] = v.strip()
    conf_gap = sorted(
        [(n, img2guid.get(i, ""), fam(i), i) for i, n in conf.items()
         if n not in JUNK and len(n) >= 2 and img2guid.get(i, "") not in eff],
        key=lambda x: (x[2], x[0]))

    # C 空间
    sp = {}
    for k, v in zh.items():
        m = re.match(r"^worm\.space\.([a-z0-9_]+)$", k)
        if m:
            sp[m.group(1)] = v.strip()
    space_gap = sorted([(n, i) for i, n in sp.items() if "name:" + n not in eff])

    # D 科技
    tech = {}
    for k, v in zh.items():
        m = re.match(r"^card\.techtile\.([a-z0-9_]+)\.title$", k)
        if m:
            tech[m.group(1)] = v.strip()
    tech_gap = sorted([(n, i) for i, n in tech.items() if "name:" + n not in eff])

    rows = []
    for n, guid, se, img in cards_gap:
        rows.append((n, n, "卡牌·" + se, "", ""))
    for n, guid, se, img in conf_gap:
        rows.append((guid or ("card.conflict." + img), n, "冲突·" + se, "", ""))
    for n, img in space_gap:
        rows.append((n, n, "空间", "", ""))
    for n, img in tech_gap:
        rows.append((n, n, "科技", "", ""))

    with open(TSV, "w", encoding="utf-8", newline="\n") as f:
        f.write("# 待补资源填写表：第 4 列填文本（70 字内），第 5 列填来源（可选）。填完跑 build_effect_map.py\n")
        f.write("# 键\t中文名\t类型\t待填文本\t来源\n")
        for r in rows:
            f.write("\t".join(r) + "\n")

    L = ["# 待补资源清单", "",
         f"共 **{len(rows)}** 条待填：卡牌 {len(cards_gap)} · 冲突 {len(conf_gap)} · 空间 {len(space_gap)} · 科技 {len(tech_gap)}", "",
         "## 怎么填（任选一种）", "",
         "1. **直接编辑** `mods/ai/kb_todo_fill.tsv`：第 4 列填文本，第 5 列填来源，然后跑 `python mods/tools/build_effect_map.py`；",
         "2. **在本文件表格里填**「待填」列，我负责转成 TSV；",
         "3. **口述给我**：说「XX 卡：效果是……」，我批量录入。", "",
         "> 格式：卡牌「特使：…；展示：…」，冲突「1st：…；2nd：…；3rd：…」。70 字内。**查不到就留空，不要猜**——空着模型会按牌名保守判断，写错反而害它。", ""]

    def table(title_, note, items):
        L.append(f"## {title_}（{len(items)} 条）")
        L.append("")
        if note:
            L.append(note)
            L.append("")
        L.append("| # | 中文名 | 键 | 现有信息 | 待填 |")
        L.append("|---|---|---|---|---|")
        for i, (name, key, info) in enumerate(items, 1):
            L.append(f"| {i} | {name} | `{key}` | {info} |  |")
        L.append("")

    table("A 卡牌效果", "填「特使：…；展示：…」，有费用写「费用N；」。",
          [(n, n, f"{se} · {img} · {guid[:8]}") for n, guid, se, img in cards_gap])
    table("B 冲突奖励", "填「1st：…；2nd：…；3rd：…」，单位用「胜利点/影响力/部队/香料/太阳币/水/阴谋牌」。\n> 同名不同卡（如 4 张「遭遇战」）靠 image 键区分：skirmish / skirmishg / skirmishh / skirmishi，填哪行就按哪行的 键 生效。",
          [(n, guid or ("card.conflict." + img), f"{se} · {img}") for n, guid, se, img in conf_gap])
    table("C 空间效果", "填放特使/触发时的效果，如「特使：获得 2 香料」。",
          [(n, n, f"版图空间 · {img}") for n, img in space_gap])
    table("D 科技牌效果", "照卡面填写。",
          [(n, n, f"科技牌 · {img}") for n, img in tech_gap])

    with open(DOC, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(L) + "\n")
    print(f"卡牌 {len(cards_gap)} · 冲突 {len(conf_gap)} · 空间 {len(space_gap)} · 科技 {len(tech_gap)} → {len(rows)} 行")
    print(DOC)
    print(TSV)


if __name__ == "__main__":
    main()
