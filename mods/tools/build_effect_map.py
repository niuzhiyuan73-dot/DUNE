"""
生成「原型 GUID -> 效果摘要」，供 AI 探针在待选列表里给每个选项附上一句"这牌干什么"。

为什么单独一张表：name_map.tsv 解决"这是什么"（渗透 / 沙虫骑士），
但模型反复说"没有具体效果文本，无法判断"——它需要"干什么"。
本地化表里就有现成的：
  卡牌  card.<set>.<image>.agent / .reveal / .text / .text1 / .text2
  领袖  worm.leader.<image>.staticability.desc / .signetability.desc
把同一个 image 的几段拼成一句，剥掉图标标签和换行，截到 EFFECT_MAX 字。

输出：mods/ai/effect_map.tsv   每行 "GUID<TAB>效果摘要"
用法：python mods/tools/build_effect_map.py
"""
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_name_map as B

ROOT = B.ROOT
OUT = os.path.join(ROOT, "mods", "ai", "effect_map.tsv")
EFFECT_MAX = 70

# 最常出现的图标 -> 中文；其余保留原名（模型看得懂 DrawCard 这类驼峰词）
SPRITE_CN = {
    "DrawCard": "抽牌",
    "Discard": "弃牌",
    "DiscardCard": "弃一张牌",
    "Strength": "军力",
    "Combat": "战斗",
    "Persuasion_1": "说服1",
    "Persuasion_2": "说服2",
    "Persuasion_3": "说服3",
    "Spice_1": "香料1",
    "Spice_2": "香料2",
    "Solari_1": "太阳币1",
    "Solari_2": "太阳币2",
    "Water_1": "水1",
    "Water_2": "水2",
    "Troop": "兵力",
    "Intrigue": "阴谋牌",
    "Influence": "影响力",
    "ControlMarkerGreen": "控制标记",
}


def clean(v):
    # 中文换行不该变成空格，否则会出现「下一 个特使」这种断词
    v = re.sub(r"<br\s*/?>", "", v, flags=re.I)
    v = re.sub(r"</?(b|i|u|size|color|line-height|alpha|voffset|nobr|space|indent)[^>]*>", "", v, flags=re.I)

    def sp(m):
        name = m.group(1)
        return SPRITE_CN.get(name, "[" + name + "]")

    v = re.sub(r'<sprite\s+name="?([^">]+)"?\s*/?>', sp, v, flags=re.I)
    v = v.replace("\\n", " ")
    v = v.replace("[IfThenArrow]", "→").replace("[name]", "")
    v = re.sub(r"\s+", " ", v).strip(" 　;；")
    return v


def build():
    cards = json.load(open(B.CARDS, encoding="utf-8-sig"))
    kv = B.load_locale()

    # image -> [(后缀, 文本)]，保持固定顺序，避免同样内容每次生成顺序不同
    by_img = {}
    for k, v in kv.items():
        m = re.match(r"^card\.[a-z]+\.([a-z0-9_]+)\.(agent|reveal|text|ability|static)(?:\.?\d+)?$", k)
        if not m:
            continue
        img, slot = m.group(1), m.group(2)
        t = clean(v)
        if t:
            by_img.setdefault(img, []).append((slot, t))

    for k, v in kv.items():
        m = re.match(r"^worm\.leader\.([a-z0-9_]+)\.(staticability|signetability)\.desc$", k)
        if not m:
            continue
        img, slot = m.group(1), m.group(2)
        t = clean(v)
        if t:
            by_img.setdefault(img, []).append((slot, t))

    order = {"agent": 0, "reveal": 1, "text": 2, "ability": 3, "static": 4,
             "staticability": 5, "signetability": 6}

    pairs = {}
    for c in cards:
        gid, img = c.get("id"), c.get("image")
        if not gid or not img or img not in by_img:
            continue
        items = sorted(by_img[img], key=lambda x: order.get(x[0], 9))
        seen, parts = set(), []
        for _, t in items:
            if t in seen:
                continue
            seen.add(t)
            parts.append(t)
        # 以「:」结尾的段是**前置条件**（阴谋牌的 text1/text2 常见），必须放到最前，
        # 否则会拼成「获得1说服；若在最高委员会放有议员指示物:」这种断句。
        conds = [p for p in parts if p.rstrip().endswith((":", "："))]
        effs = [p for p in parts if p not in conds]
        if conds and effs:
            head = "，".join(c.rstrip(":：").strip() for c in conds)
            text = head + "：" + "；".join(effs)
        elif conds:
            text = ""                     # 只有条件、没有效果，宁可不写
        else:
            text = "；".join(effs)
        if len(text) > EFFECT_MAX:
            text = text[:EFFECT_MAX] + "…"
        if text:
            pairs[gid] = text

    # 很多卡有多个 GUID（不同版本/复刻），但只有其中一个有文本；
    # 再补一份「中文名 -> 效果」，插件按 GUID 查不到时退回按名字查。
    name_of = {}
    try:
        for line in open(os.path.join(ROOT, "mods", "ai", "name_map.tsv"), encoding="utf-8"):
            k, _, v = line.rstrip("\n").partition("\t")
            if k.count("-") == 4 and v:
                name_of[k] = v
    except OSError:
        pass

    by_name = {}
    for gid, text in pairs.items():
        nm = name_of.get(gid)
        if nm and nm not in by_name:
            by_name[nm] = text
    for nm, text in by_name.items():
        pairs.setdefault("name:" + nm, text)

    # 人工补充表：基础包卡牌在本地化里没有文本，只能靠公开卡表补（见文件头注释）。
    # 表里用英文卡名做键，这里借 image 反查官方中文名，避免手写译名对不上。
    extra = os.path.join(ROOT, "mods", "ai", "card_text_extra.tsv")
    added, unmatched = 0, []
    if os.path.exists(extra):
        en = B.load_locale("en_US")
        en_title_img = {}
        for k, v in en.items():
            m = re.match(r"^card\.[a-z]+\.([a-z0-9_]+)\.title$", k)
            if m:
                en_title_img.setdefault(v.strip().lower(), m.group(1))

        zh_img_title = {}
        for k, v in kv.items():
            m = re.match(r"^card\.[a-z]+\.([a-z0-9_]+)\.title$", k)
            if m:
                zh_img_title.setdefault(m.group(1), v.strip())

        for line in open(extra, encoding="utf-8-sig"):
            line = line.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) < 2 or not parts[0].strip() or not parts[1].strip():
                continue
            en_name = parts[0].strip()
            img = en_title_img.get(en_name.lower())
            zh_name = zh_img_title.get(img) if img else None
            if not zh_name:
                unmatched.append(en_name)
                zh_name = en_name
            key = "name:" + zh_name
            if key in pairs:
                continue
            text = parts[1].strip()
            if len(text) > EFFECT_MAX:
                text = text[:EFFECT_MAX] + "…"
            pairs[key] = text
            added += 1
        if unmatched:
            print("补充表里无法对应到中文名的条目:", ", ".join(unmatched))

    # 第二张补充表：直接按「中文名」索引，用于冲突牌奖励、空间效果、科技、合约等
    # 非卡牌实体（它们没有英文标题可桥接）。格式：中文名\t文本\t来源
    kb_extra = os.path.join(ROOT, "mods", "ai", "kb_extra.tsv")
    kb_added = 0
    if os.path.exists(kb_extra):
        for line in open(kb_extra, encoding="utf-8-sig"):
            line = line.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) < 2 or not parts[0].strip() or not parts[1].strip():
                continue
            raw = parts[0].strip()
            # 冲突牌同名跨扩展，必须用 GUID 作键；其余实体用中文名
            key = raw if raw.count("-") == 4 else "name:" + raw
            if key in pairs:
                continue
            text = parts[1].strip()
            if len(text) > EFFECT_MAX:
                text = text[:EFFECT_MAX] + "…"
            pairs[key] = text
            kb_added += 1

    # 第三张表：待补清单（kb_todo_fill.tsv）。第 1 列键、第 4 列填好的文本；空着就跳过。
    todo = os.path.join(ROOT, "mods", "ai", "kb_todo_fill.tsv")
    todo_added = 0
    if os.path.exists(todo):
        for line in open(todo, encoding="utf-8-sig"):
            line = line.rstrip("\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            parts = line.split("\t")
            if len(parts) < 4:
                continue
            raw, text = parts[0].strip(), parts[3].strip()
            if not raw or not text:
                continue
            key = raw if raw.count("-") == 4 else "name:" + raw
            if key in pairs:
                continue
            if len(text) > EFFECT_MAX:
                text = text[:EFFECT_MAX] + "…"
            pairs[key] = text
            todo_added += 1



    # >>> 特使可去工位（catalog_probe 920004 + InfiltrateAbility）
    AGENT_ICONS = {"Circle": "城市", "Triangle": "香料贸易", "Pentagon": "兰兹拉德",
                   "Emperor": "皇帝", "SpacingGuild": "宇航公会", "BeneGesserit": "贝尼·杰瑟里特",
                   "Fremen": "弗雷曼人", "Spy": "间谍"}
    try:
        import json as _json
        _ar_p = os.path.join(ROOT, "dump", "current_mode", "archetypes.json")
        _ab_p = os.path.join(ROOT, "dump", "current_mode", "ability_observations.json")
        _img_icons, _img_infil = {}, set()
        if os.path.exists(_ar_p):
            _ar = _json.load(open(_ar_p, encoding="utf-8-sig"))
            _ab = _json.load(open(_ab_p, encoding="utf-8-sig")) if os.path.exists(_ab_p) else {}
            _infil_ids = set()
            for _k, _v in _ab.items():
                if _v and "Infiltrate" in str(_v[0].get("attributes", {}).get("Name")):
                    _infil_ids.add(_k)
            for _a in _ar:
                _at = {x["name"]: x["value"] for x in (_a.get("attributes") or [])}
                _img = _at.get(920003)
                if not _img:
                    continue
                _ic = [AGENT_ICONS[t] for t in (_at.get(920004) or []) if t in AGENT_ICONS]
                if _ic:
                    _img_icons[_img] = _ic
                if _infil_ids & set(_at.get(900013) or []):
                    _img_infil.add(_img)

        # 中文名 -> image（card.<set>.<image>.title 反查）
        _zh2img = {}
        for _k, _v in kv.items():
            _m = re.match(r"^card\.[a-z]+\.([a-z0-9_]+)\.title$", _k)
            if _m:
                _zh2img[_v.strip()] = _m.group(1)

        _prefixed = 0
        for _key in list(pairs.keys()):
            if not _key.startswith("name:"):
                continue
            _txt = pairs[_key]
            if _txt.startswith("特使可去"):
                continue
            _img = _zh2img.get(_key[5:])
            if not _img:
                continue
            _icons = _img_icons.get(_img)
            _parts = []
            if _img in _img_infil:
                _parts.append("特使可去：任意位置（渗透）")
            elif _icons:
                _parts.append("特使可去：" + "、".join(_icons))
            else:
                continue
            if _icons and "间谍" in _icons:
                _parts.append("打出派间谍")
            pairs[_key] = "；".join(_parts) + "。" + _txt
            _prefixed += 1
        print("特使可去前缀：%d 条（图标 %d / 渗透 %d）" % (_prefixed, len(_img_icons), len(_img_infil)))
    except Exception as _e:
        print("特使图标前缀失败（不影响主流程）:", _e)
    # <<< 特使可去工位
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        for k in sorted(pairs):
            v = pairs[k].replace("\\", "\\\\").replace("\t", " ").replace("\n", " ")
            f.write("%s\t%s\n" % (k, v))

    print("写出 %d 条（卡表 %d / kb %d / 待补表 %d）-> %s" % (len(pairs), added, kb_added, todo_added, OUT))


if __name__ == "__main__":
    build()
