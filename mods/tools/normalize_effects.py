"""
把 effect_map_v2.tsv（整段中文文本）归一成 effect_map_v3.tsv（文本 + 结构化字段）。

解决三个实测问题（2026-11-01 复盘，305 条问答取证）：

1) 数量选择器被模型读错。原文「1st：选择两个：皇帝/宇航公会/姐妹会/弗雷曼轨道各前进1」
   会被读成"四家全部+1"（seq15 实测），而正确是"选其中两个"。改写成
   「选择两个（只选2个，不要全选）：…」，把"不要全选"写进文本，不依赖模型常识。
2) 图标词是机器标识。「[Water]」「[FactionRankUp]」这类方括号图标不在 Humanize 的处理
   范围内（它只处理 <sprite name=...>），于是中英混排进入 prompt。这里按映射表转中文，
   未收录的原样保留（不猜）。
3) 费用/条件埋在长句里，模型每次都要现场解析。「（费用6香料）」「如果你有一个同盟阵营：」
   抽成独立列，运行时拼成「｜花费 6香料｜条件 …」，模型不必再从句子里找。

原则：
- 只做"结构化重排"，不改写规则语义；抽取不到就留空，不猜。
- 幂等：对 v3 的正文本再跑一次必须完全不变（脚本自带自检）。
- v2 保持不变，C# 侧优先读 v3，读不到回退 v2。

用法：
  python -X utf8 -B mods/tools/normalize_effects.py            # 生成 v3
  python -X utf8 -B mods/tools/normalize_effects.py --dry-run  # 只打印样例，不写文件
"""
import os
import re
import sys
import collections

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
IN_V2 = os.path.join(ROOT, "mods", "ai", "effect_map_v2.tsv")
OUT_V3 = os.path.join(ROOT, "mods", "ai", "effect_map_v3.tsv")

# ---------- 1. 方括号图标 -> 中文 ----------
RES_BASE = {
    "Water": "水", "Spice": "香料", "Solari": "太阳币", "Persuasion": "说服",
    "VictoryPoint": "胜利点", "Influence": "影响力", "Troop": "部队",
    "IntrigueCard": "阴谋牌", "TrashCard": "销毁牌", "WarpSpaceCard": "跃迁空间牌",
    "Contract": "合约", "Worm": "沙虫", "Signet": "印章戒指", "MakerHook": "造物主钩杖",
    "BuyTech": "购买科技", "Freighter": "货船", "TechNegotiator": "科技谈判者",
    "ShieldWallBreak": "破盾", "Spy": "间谍", "RecallSpy": "收回间谍", "RecallAgent": "收回特使",
}
FACTION_CN = {
    "Emperor": "皇帝", "SpacingGuild": "宇航公会", "BeneGesserit": "姐妹会", "Fremen": "弗雷曼",
}
PLACE_CN = {"Landsraad": "兰兹拉德", "PopulationCenter": "聚居地", "Commerce": "商会"}
MISC_CN = {"ArrowDown": "↓", "x": "×", "WildRankUp": "任意阵营轨道+1",
           "WildRankDown": "任意阵营轨道-1"}

BRACKET_RE = re.compile(r"\[([A-Za-z_0-9]+)\]")


def bracket_cn(tok):
    if tok in RES_BASE:
        return RES_BASE[tok]
    m = re.match(r"^([A-Za-z]+)_(\d+)$", tok)
    if m and m.group(1) in RES_BASE:
        return RES_BASE[m.group(1)] + m.group(2)
    if tok in FACTION_CN:
        return FACTION_CN[tok]
    if tok in PLACE_CN:
        return PLACE_CN[tok]
    if tok in MISC_CN:
        return MISC_CN[tok]
    m = re.match(r"^([A-Za-z]+)Rank(Up|Down)$", tok)
    if m and m.group(1) in FACTION_CN:
        return FACTION_CN[m.group(1)] + ("轨道+1" if m.group(2) == "Up" else "轨道-1")
    return None


# ---------- 2. 数量选择器归一 ----------
CN_NUM = {"一": 1, "二": 2, "两": 2, "三": 3, "四": 4, "五": 5, "六": 6}
# 只匹配"选择N个/项："这种带冒号的选择器；不带冒号的（"选择两个派系轨道各前进1"）另处理
PICK_COLON_RE = re.compile(r"选择([一二两三四五六])(个|项)?\s*[:：]")
PICK_PLAIN_RE = re.compile(r"选择([一二两三四五六])(个|项)(?!\s*[:：])")


def _pick_sub(m):
    n = CN_NUM[m.group(1)]
    tail = m.group(0)[-1] if m.group(0).endswith((":", "：")) else ""
    return "选%d项（只选%d个，不要全选）%s" % (n, n, tail)


def normalize_text(t):
    """幂等：已含「只选N个」标记的段落不再二次改写。"""
    if not t:
        return t

    # 2.1 方括号图标
    def _br(m):
        cn = bracket_cn(m.group(1))
        return cn if cn else m.group(0)
    t = BRACKET_RE.sub(_br, t)

    # 2.2 数量选择器（跳过已经标注过的）
    if "只选" not in t:
        t = PICK_COLON_RE.sub(_pick_sub, t)
        t = PICK_PLAIN_RE.sub(_pick_sub, t)

    # 2.3 标点与空白清理
    t = re.sub(r"[；;]{2,}", "；", t)
    t = re.sub(r"[，,]{2,}", "，", t)
    t = re.sub(r"\s{2,}", " ", t)
    t = re.sub(r"\s*([；：，。])\s*", r"\1", t)
    return t.strip(" ；")


# ---------- 3. 花费 / 条件抽取 ----------
# 只认"付费"语境。中文卡面里「获得 2 香料」是收益不是花费，
# 单纯按「数字+单位」匹配会把收益记成花费（比漏抽更糟），所以要求前缀词。
COST_RE = re.compile(r"(\d+)\s*点?\s*(说服|香料|太阳币|水)")
COST_LEAD_RE = re.compile(r"(费用|支付|花费|需要?)\s*[:：]?\s*$")
COST_HEAD_RE = re.compile(r"^\s*(\d+)\s*(说服|香料|太阳币|水)\s*[:：]")
COND_RE = re.compile(r"(?:如果|若|当你|当你有|只有|需要有|需有)[^：:，,；;。]{2,20}")
COND_BLACK = {"若如此做", "若还未放置", "若满足以上3项", "若所有"}


def extract_cost(t):
    """返回去重后的花费描述（最多两条），如 "6香料"、"2香料/1水"。

    判定为花费的三种情形：前缀是 费用/支付/花费/需；或文本以 "N单位：" 开头
    （卡面写法 "2香料:+5军力"）。其余一律不记。
    """
    if not t:
        return ""
    hits = []
    m = COST_HEAD_RE.match(t)
    if m:
        hits.append("%s%s" % (m.group(1), m.group(2)))
    for mm in COST_RE.finditer(t):
        if not COST_LEAD_RE.search(t[max(0, mm.start() - 6):mm.start()]):
            continue
        s = "%s%s" % (mm.group(1), mm.group(2))
        if s not in hits:
            hits.append(s)
    return "/".join(hits[:2])


def extract_cond(t):
    """抽一句触发/前提条件。碎片（含省略号、括号未闭合、纯过渡语）一律丢弃。"""
    if not t:
        return ""
    for m in COND_RE.finditer(t):
        c = m.group(0).strip(" ：,，")
        if len(c) < 4 or c in COND_BLACK:
            continue
        if "..." in c or "…" in c or c.endswith("）") or c.endswith("("):
            continue
        return c if len(c) <= 24 else ""
    return ""


# ---------- 4. 主流程 ----------
def build(dry_run=False):
    if not os.path.exists(IN_V2):
        raise SystemExit("找不到 %s，先跑 build_effect_map.py" % IN_V2)

    out = []
    stats = collections.Counter()
    samples = []
    for line in open(IN_V2, encoding="utf-8"):
        line = line.rstrip("\n")
        if not line.strip():
            continue
        c = line.split("\t")
        if len(c) < 6:
            stats["坏行"] += 1
            continue
        key, src, prio, ext, amb, text = c[0], c[1], c[2], c[3], c[4], c[5]
        norm = normalize_text(text)
        # 幂等自检：再跑一次必须不变
        if normalize_text(norm) != norm:
            stats["非幂等"] += 1
        if norm != text:
            stats["正文改写"] += 1
            if len(samples) < 6:
                samples.append((text, norm))
        cost = extract_cost(norm)
        cond = extract_cond(norm)
        if cost:
            stats["有花费"] += 1
        if cond:
            stats["有条件"] += 1
        out.append([key, src, prio, ext, amb, norm, cost, cond])

    print("读入 %d 条" % len(out))
    for k, v in stats.most_common():
        print("  %s: %d" % (k, v))
    for a, b in samples:
        print("  - 原: %s" % a[:90])
        print("    新: %s" % b[:90])

    if dry_run:
        print("dry-run，不写文件")
        return

    with open(OUT_V3, "w", encoding="utf-8", newline="\n") as f:
        for r in out:
            f.write("\t".join(r) + "\n")
    print("写出 %d 条 -> %s" % (len(out), OUT_V3))


if __name__ == "__main__":
    build("--dry-run" in sys.argv)
