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
import collections

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_name_map as B

ROOT = B.ROOT
OUT = os.path.join(ROOT, "mods", "ai", "effect_map.tsv")
OUT_V2 = os.path.join(ROOT, "mods", "ai", "effect_map_v2.tsv")
OUT_AMB = os.path.join(ROOT, "mods", "ai", "ambiguous_names.tsv")
EFFECT_MAX = 260  # 2026-11-01 复核：110 会把领袖/多段效果卡腰斩（杰西卡夫人、泰西娅等），放宽

# 来源优先级（数字越大越权威）。
# 旧实现把自动提取（本地化）先写入 pairs，人工补充表后写入却被 `if key in pairs: continue`
# 跳过 —— 方向完全反了：不完整的旧文本会挡住人工核对后的完整文本。
# 实测曾有 18 条 card_text_extra 与 15 条 kb_extra 条目被自动提取挡住。
#
# 新的合并策略：同键多源时取高优先级者；同优先级按字段拼接互补内容，不丢字段。
SRC_PRIORITY = {
    "auto": 10,        # 本地化自动提取
    "card_text_extra": 20,   # 人工补充（英文名桥接）
    "kb_extra": 30,    # 人工补充（中文名索引）
    "kb_todo_fill": 40,      # 人工复核填表（最高）
}
SRC_LABEL = {
    "auto": "本地化自动提取",
    "card_text_extra": "人工补充表card_text_extra",
    "kb_extra": "人工补充表kb_extra",
    "kb_todo_fill": "人工复核填表kb_todo_fill",
}

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


def _split_segments(text):
    """按中文分号与句号切段。

    人工填写的文本混用「；」和「。」两种分隔符（例："特使可去：城市。特使：..."），
    只按一种切会把前缀段和效果段粘在一起，导致合并时前缀重复。
    """
    if not text:
        return []
    out = []
    for chunk in text.replace("；", "。").split("。"):
        c = chunk.strip()
        if c:
            out.append(c)
    return out


def build():
    cards = json.load(open(B.CARDS, encoding="utf-8-sig"))
    kv = B.load_locale()

    # 人工复核锁：kb_todo_fill.tsv 里第 4 列非空的键视为已人工核实，
    # 优先级最高，前面的自动提取源（本地化/卡表/kb_extra）不得覆盖。
    LOCK = set()
    _todo_p = os.path.join(ROOT, "mods", "ai", "kb_todo_fill.tsv")
    if os.path.exists(_todo_p):
        for _l in open(_todo_p, encoding="utf-8-sig"):
            _p = _l.rstrip("\n").split("\t")
            if len(_p) >= 4 and _p[0].strip() and _p[3].strip():
                _raw = _p[0].strip()
                _raw = _raw[5:] if _raw.startswith("name:") else _raw
                LOCK.add(_raw if _raw.count("-") == 4 else "name:" + _raw)
                LOCK.add("name:" + _raw)

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

    # ============ 统一合并框架 ============
    # 每条记录带来源与优先级；高优先级覆盖低优先级，同级按字段互补合并。
    # 记录结构：key -> {"text": 合并后文本, "src": 来源标签, "prio": 优先级,
    #                 "fields": {字段名: 文本}, "ambiguous": 是否同名歧义}
    records = {}

    def put(key, text, src, fields=None):
        """按优先级合并一条来源。高优先级直接覆盖；同级则互补拼接。"""
        if not key or not text:
            return
        text = text.strip()
        if not text:
            return
        prio = SRC_PRIORITY.get(src, 0)
        rec = records.get(key)
        if rec is None:
            records[key] = {"text": text, "src": src, "prio": prio,
                            "fields": dict(fields or {}), "ambiguous": False}
            return
        if prio > rec["prio"]:
            # 更高优先级的人工核对结果覆盖旧的自动提取文本
            rec["text"] = text
            rec["src"] = src
            rec["prio"] = prio
            rec["fields"].update(fields or {})
        elif prio == rec["prio"]:
            # 同级：按段互补合并，保留双方信息，但跳过重复段。
            # 不能简单拼接整条文本 —— "特使可去：X" 这类前缀段会重复出现，
            # 而同名不同扩展的两条文本前缀还可能不一致（皇帝、姐妹会 vs 皇帝、贝尼·杰瑟里特）。
            existing = [s for s in _split_segments(rec["text"]) if s]
            incoming = [s for s in _split_segments(text) if s]
            added = False
            # 前缀段（"特使可去：X"）同一键只能有一个。
            # 自动提取的 name: 汇总文本自带前缀，人工表文本也自带前缀，
            # 直接拼接会出现两个前缀且图标可能不一致（旧数据里即有此问题）。
            # 规则：新文本的前缀段替换旧的前缀段，其余段按出现顺序保留。
            def _has_prefix(segs):
                return any(s.startswith("特使可去") for s in segs)

            if _has_prefix(incoming):
                old_prefix = [s for s in existing if s.startswith("特使可去")]
                rest_existing = [s for s in existing if not s.startswith("特使可去")]
                new_prefix = [s for s in incoming if s.startswith("特使可去")]
                rest_incoming = [s for s in incoming if not s.startswith("特使可去")]
                existing = new_prefix + rest_existing
                incoming = rest_incoming
                if old_prefix and old_prefix != new_prefix:
                    rec["src"] = rec["src"] + "+" + src
            for s in incoming:
                if s in existing:
                    continue
                existing.append(s)
                added = True
            if added:
                rec["text"] = "；".join(existing)
                rec["src"] = rec["src"] + "+" + src
            rec["fields"].update(fields or {})

    # --- 来源1：本地化自动提取 ---
    for c in cards:
        gid, img = c.get("id"), c.get("image")
        if not gid or not img or img not in by_img:
            continue
        items = sorted(by_img[img], key=lambda x: order.get(x[0], 9))
        seen, parts, fields = set(), [], {}
        for slot, t in items:
            if t in seen:
                continue
            seen.add(t)
            parts.append(t)
            fields.setdefault(slot, t)
        # 以「:」结尾的段是**前置条件**（阴谋牌的 text1/text2 常见），必须放到最前，
        # 否则会拼成「获得1说服；若在最高委员会放有议员指示物:」这种断句。
        conds = [p for p in parts if p.rstrip().endswith((":", "："))]
        effs = [p for p in parts if p not in conds]
        if conds and effs:
            head = "，".join(x.rstrip(":：").strip() for x in conds)
            text = head + "：" + "；".join(effs)
        elif conds:
            text = ""                     # 只有条件、没有效果，宁可不写
        else:
            text = "；".join(effs)
        if text:
            put(gid, text, "auto", fields)

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

    # 同名 -> GUID 列表。用于消歧：一个中文名对应多个 GUID 且文本不同时，
    # name: 汇总键必然丢信息，不生成（C# 侧改为按 GUID 精确查）。
    gids_by_name = {}
    for gid, nm in name_of.items():
        gids_by_name.setdefault(nm, []).append(gid)

    # 注：歧义判定与 name: 汇总键的生成都推迟到所有来源合并之后，
    # 否则只能看到本地化来源，补充表带来的同名冲突会漏判。

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
            put(key, parts[1].strip(), "card_text_extra")
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
            put(key, parts[1].strip(), "kb_extra")
            kb_added += 1

    # 第三张表：待补清单（kb_todo_fill.tsv）。第 1 列键、第 4 列填好的文本；空着就跳过。
    # 这里已填的键即 LOCK —— 人工复核结果，优先级最高，会覆盖自动提取的旧文本。
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
            put(key, text, "kb_todo_fill")
            todo_added += 1

    # ============ 歧义判定（所有来源合并完成后）============
    # 一个中文名对应多个 GUID，且这些 GUID 的规则文本不同 -> 按名兜底必然串用。
    # 这种情况不生成 name: 汇总键；C# 侧改为按 GUID 精确查，查不到就标歧义。
    ambiguous_names = set()
    for nm, gids in gids_by_name.items():
        if len(gids) < 2:
            continue
        texts = {records[g]["text"] for g in gids if g in records and records[g]["text"]}
        if len(texts) > 1:
            ambiguous_names.add(nm)

    # name: 汇总键（仅供确实没有 GUID 的非卡牌实体，以及无歧义的卡）。
    # 注意：name: 键的来源优先级低于人工补充表，因此用 put() 合并，
    # 自动提取不会挡住人工核对后的完整文本。
    by_name = {}
    for gid, rec in records.items():
        if gid.startswith("name:"):
            continue
        nm = name_of.get(gid)
        if nm and nm not in by_name:
            by_name[nm] = rec["text"]
    for nm, text in by_name.items():
        if nm in ambiguous_names:
            continue
        put("name:" + nm, text, "auto")


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
        for _key in list(records.keys()):
            if not _key.startswith("name:"):
                continue
            _rec = records[_key]
            _txt = _rec["text"]
            # 去重：同一键只保留一个"特使可去"前缀段。
            # 人工表文本自带前缀（且常不在段首，例如"特莱拉牌（…）。特使可去：城市。特使：…"），
            # 与自动前缀叠加后会出现两个前缀，旧版 effect_map.tsv 里即有此问题。
            _segs = _split_segments(_txt)
            _prefix_segs = [s for s in _segs if s.startswith("特使可去")]
            if len(_prefix_segs) > 1:
                _seen_prefix = False
                _keep = []
                for _s in _segs:
                    if _s.startswith("特使可去"):
                        if _seen_prefix:
                            continue
                        _seen_prefix = True
                    _keep.append(_s)
                _txt = "；".join(_keep)
                _rec["text"] = _txt
            # 幂等：文本任一段已是"特使可去"前缀就不再加前缀
            if _prefix_segs:
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
            _rec["text"] = "；".join(_parts) + "。" + _txt
            _prefixed += 1
        print("特使可去前缀：%d 条（图标 %d / 渗透 %d）" % (_prefixed, len(_img_icons), len(_img_infil)))
    except Exception as _e:
        print("特使图标前缀失败（不影响主流程）:", _e)
    # <<< 特使可去工位

    # 扩展与版本信息：原型 GUID -> 内容集（同名卡跨扩展时用于消歧）
    sets_of = {}
    try:
        import json as _json
        _ar_p = os.path.join(ROOT, "dump", "current_mode", "archetypes.json")
        _df_p = os.path.join(ROOT, "dump", "current_mode", "attribute_definitions.json")
        if os.path.exists(_ar_p):
            _defs = {}
            if os.path.exists(_df_p):
                for _d in _json.load(open(_df_p, encoding="utf-8-sig")):
                    _defs[str(_d["key"])] = _d["name"]
            for _a in _json.load(open(_ar_p, encoding="utf-8-sig")):
                _at = {}
                for _x in (_a.get("attributes") or []):
                    _at[_defs.get(str(_x["name"]), str(_x["name"]))] = _x["value"]
                _sl = _at.get("SetList")
                if isinstance(_sl, list) and _sl:
                    sets_of[_a["id"]] = "/".join(str(s) for s in _sl)
    except Exception as _e:
        print("扩展信息读取失败（不影响主流程）:", _e)

    # 同名歧义标记：这些中文名对应多个 GUID 且规则不同，按名兜底会串用，
    # C# 侧据此拒绝用中文名取效果。
    #
    # 注意：人工补充表用中文名作键时无法指定版本（如"沙漠力量"横跨 Uprising/BaseSet），
    # 这类键同样必须标记歧义 —— 否则 C# 按名兜底会拿到另一个扩展的规则。
    amb_flag = {("name:" + n): n for n in ambiguous_names}
    for _key in records:
        if not _key.startswith("name:"):
            continue
        _nm = _key[5:]
        _gids = gids_by_name.get(_nm, [])
        if len(_gids) > 1:
            amb_flag.setdefault(_key, _nm)

    # ---- 写出 v1：旧两列格式，C# 可直接读（回退路径）----
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        for k in sorted(records):
            v = records[k]["text"].replace("\\", "\\\\").replace("\t", " ").replace("\n", " ")
            # 旧格式没有元信息列，长文本只能就地截断——这是发送侧的预算问题，
            # 完整内容请用 v2。
            if len(v) > EFFECT_MAX:
                v = v[:EFFECT_MAX] + "…"
            f.write("%s\t%s\n" % (k, v))

    # ---- 写出 v2：带来源/优先级/扩展/歧义标记，长文本完整保留 ----
    # 格式：键<TAB>来源<TAB>优先级<TAB>扩展<TAB>歧义标记<TAB>文本
    with open(OUT_V2, "w", encoding="utf-8", newline="\n") as f:
        for k in sorted(records):
            rec = records[k]
            src = SRC_LABEL.get(rec["src"].split("+")[0], rec["src"])
            if "+" in rec["src"]:
                src = "+".join(SRC_LABEL.get(s, s) for s in rec["src"].split("+"))
            ext = sets_of.get(k, "")
            amb = amb_flag.get(k, "")
            v = rec["text"].replace("\\", "\\\\").replace("\t", " ").replace("\n", " ")
            f.write("%s\t%s\t%d\t%s\t%s\t%s\n" % (k, src, rec["prio"], ext, amb, v))

    # ---- 写出歧义清单：供 C# 与人工核对 ----
    with open(OUT_AMB, "w", encoding="utf-8", newline="\n") as f:
        f.write("# 同名多版本卡：中文名对应多个 GUID 且规则不同，禁止按名兜底取效果\n")
        for nm in sorted(ambiguous_names):
            gids = sorted(gids_by_name.get(nm, []))
            f.write("%s\t%s\n" % (nm, ",".join(gids)))

    src_counts = collections.Counter()
    for rec in records.values():
        src_counts[rec["src"]] += 1
    print("写出 %d 条 -> %s" % (len(records), OUT))
    print("写出 %d 条（含来源/优先级/扩展，长文本不截断）-> %s" % (len(records), OUT_V2))
    print("来源分布: " + ", ".join("%s=%d" % (SRC_LABEL.get(k, k), v)
                                  for k, v in src_counts.most_common()))
    print("同名歧义组: %d（已从 name: 汇总键中剔除，改用 GUID 精确查）" % len(ambiguous_names))
    print("补充表条目: card_text_extra %d / kb_extra %d / 待补表 %d" % (added, kb_added, todo_added))


if __name__ == "__main__":
    build()
