"""
抓取冲突牌奖励（1st/2nd/3rd），按「卡名 + 扩展」双键配对到本地 GUID。

为什么不能只按卡名：冲突牌跨扩展大量同名（Skirmish×4、Siege of Arrakeen×2 等），
而外部站点的 slug 与页面内容并不总对应（例：/cards/438/...siege-of-carthag 返回 Grand Vision）。
所以每个候选页都必须**先在页面内校验卡名**，再按扩展族配对。

扩展族取值：baseset / uprising / ixs / immortality（来自 cards_runtime.json 的 sets 字段；
站点 URL 前缀 dune-imperium- / uprising- / ix- / immortality-）。

输出：
  dump/tmp/conf_pages.tsv      通过校验的页面（卡名/族/等级/奖励/URL）
  dump/tmp/conf_assigned.tsv   唯一配对成功（GUID<TAB>中文名<TAB>奖励<TAB>URL）
  dump/tmp/conf_ambiguous.md   歧义清单（交人工判定）
"""
import html
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "mods", "tools"))
import build_name_map as B

TMP = os.path.join(ROOT, "dump", "tmp")
SITEMAP = os.path.join(TMP, "sm.xml")
CARDS = os.path.join(ROOT, "dump", "cards_runtime.json")

MAP = [("place your Control marker on the flag below", "在下方旗帜放控制标记："),
       ("place you Control marker on the flag below", "在下方旗帜放控制标记："),
       ("Victory Points", "胜利点"), ("Victory Point", "胜利点"),
       ("trash one card from your hand, discard pile, or in play", "从手牌/弃牌堆/场上销毁1张牌"),
       ("draw 2 Intrigue", "抽2张阴谋牌"), ("draw 1 Intrigue", "抽1张阴谋牌"),
       ("Intrigue cards", "阴谋牌"), ("Intrigue card", "阴谋牌"), ("Intrigue", "阴谋牌"),
       ("gain 1 influence with any Faction", "任意阵营影响力1"),
       ("gain 2 influence with any one Faction", "任意一个阵营影响力2"),
       ("gain 1 influence with Emperor", "皇帝影响力1"),
       ("gain 1 influence with Spacing Guild", "宇航公会影响力1"),
       ("gain 1 influence with Bene Gesserit", "贝尼·杰瑟里特影响力1"),
       ("gain 1 influence with Fremen", "弗雷曼影响力1"),
       ("with any Faction", "任意阵营"), ("with Emperor", "皇帝"),
       ("with Spacing Guild", "宇航公会"), ("with Bene Gesserit", "贝尼·杰瑟里特"),
       ("with Fremen", "弗雷曼"),
       ("choose two:", "四选二："), (" or ", " 或 "),
       ("Arrakeen", "厄拉奇恩"), ("Carthag", "迦太格"), ("Imperial Basin", "帝国盆地"),
       ("water", "水"), ("spice", "香料"), ("Solari", "太阳币"), ("solari", "太阳币"),
       ("troops", "部队"), ("troop", "部队"), ("Influence", "影响力"),
       ("Control marker", "控制标记"), ("gain", "获得"), (" and ", "、"),
       ("recruited from your supply to your garrison", "从供应堆进兵营")]

FAMILY = {"dune-imperium": "baseset", "uprising": "uprising", "ix": "ixs", "immortality": "immortality",
          "rise-of-ix": "ixs", "bloodlines": "bloodlines"}


def norm(s):
    return re.sub(r"[^a-z0-9]+", "-", s.lower()).strip("-")


def zh_reward(s):
    parts = re.split(r"(?=(?:1st|2nd|3rd) place:)", re.sub(r"\s+", " ", s).strip())
    out = []
    for p in parts:
        m = re.match(r"(1st|2nd|3rd) place:\s*(.*)", p.strip())
        if not m:
            continue
        body = m.group(2)
        for a, b in MAP:
            body = body.replace(a, b)
        out.append(m.group(1) + "：" + body.strip(" .,"))
    return "；".join(out)


def family_of_url(url):
    slug = re.search(r"/cards/\d+/(.+)$", url).group(1)
    for prefix, fam in FAMILY.items():
        if slug.startswith(prefix):
            return fam
    return slug.split("-")[0]


def load_conflicts():
    en, zh = B.load_locale("en_US"), B.load_locale()
    cards = json.load(open(CARDS, encoding="utf-8-sig"))
    img2c = {}
    for c in cards:
        if c.get("image"):
            img2c.setdefault(c["image"], []).append(c)

    out = {}
    for k, v in en.items():
        m = re.match(r"^card\.conflict\.([a-z0-9_]+)$", k)
        if not m:
            continue
        img, name = m.group(1), v.strip()
        if len(name) < 4:          # "st"/"nd"/"rd" 这类本地化碎片，不是真卡名
            continue
        cs = img2c.get(img, [])
        sets = set()
        for c in cs:
            sets.update(str(x).lower() for x in (c.get("sets") or []))
        out[img] = {
            "en": name,
            "zh": next((x.strip() for kk, x in zh.items() if kk == "card.conflict." + img), ""),
            "guids": [c["id"] for c in cs],
            "family": sorted(sets)[0] if sets else "baseset",
        }
    return out


def main():
    os.makedirs(TMP, exist_ok=True)
    if not os.path.exists(SITEMAP):
        sys.exit("缺少 dump/tmp/sm.xml，先下载 sitemap")
    slugs = {}
    for u in re.findall(r"<loc>([^<]+)</loc>", open(SITEMAP, encoding="utf-8").read()):
        m = re.search(r"/cards/\d+/(.+)$", u)
        if m:
            slugs.setdefault(m.group(1), u)

    conf = load_conflicts()
    pages = []          # (en_name, family, level, rewards, url)
    seen_url = set()

    for img, d in conf.items():
        cands = [u for s, u in slugs.items() if s.endswith(norm(d["en"]))]
        for url in cands:
            if url in seen_url:
                continue
            seen_url.add(url)
            body = subprocess.run(["curl", "-s", "--max-time", "25", url],
                                  capture_output=True).stdout.decode("utf-8", "replace")
            m = re.search(r'<meta name="description" content="([^"]*)"', body)
            if not m:
                continue
            desc = html.unescape(m.group(1))
            # 页内校验：描述必须以目标卡名开头，否则是 slug 错位的页
            if not desc.lower().startswith(d["en"].lower() + " "):
                print(f"  [skip mismatched] {url} -> {desc[:48]}")
                continue
            i = desc.find("rewards")
            if i < 0:
                continue
            lv = re.search(r"\(Conflict\s*([IVX]+)\)", desc)
            rw = zh_reward(desc[i + 7:])
            if not rw:
                continue
            # 元描述被截断时会留下 "3rd：ga" 这种残句，宁可不要
            if not rw.startswith("1st："):      # 缺头名奖励的数据不可用
                print(f"  [skip no-1st] {url}")
                continue
            if any(len(seg.split("：", 1)[1].strip()) < 3 for seg in rw.split("；") if "：" in seg):
                print(f"  [skip truncated] {url}")
                continue
            pages.append((d["en"], family_of_url(url), lv.group(1) if lv else "", rw, url))

    with open(os.path.join(TMP, "conf_pages.tsv"), "w", encoding="utf-8", newline="\n") as f:
        for p in pages:
            f.write("\t".join(p) + "\n")

    # 配对：卡名 + 扩展族
    assigned, ambig = [], []
    for img, d in conf.items():
        matched = [p for p in pages if p[0] == d["en"] and p[1] == d["family"]]
        if len(matched) == 1:
            assigned.append((d["guids"][0] if d["guids"] else "", d["zh"], matched[0][3], matched[0][4]))
        elif len(matched) > 1:
            ambig.append((img, d, matched, "同族多页"))
        else:
            other = [p for p in pages if p[0] == d["en"]]
            ambig.append((img, d, other, "同族无页" + (f"（他族有 {len(other)} 页）" if other else "")))

    with open(os.path.join(TMP, "conf_assigned.tsv"), "w", encoding="utf-8", newline="\n") as f:
        for a in assigned:
            f.write("\t".join(a) + "\n")

    with open(os.path.join(TMP, "conf_ambiguous.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("# 冲突奖励：需要人工确认的条目\n\n")
        f.write(f"已唯一配对 {len(assigned)} 条，待确认 {len(ambig)} 条。\n\n")
        for img, d, matched, why in ambig:
            f.write(f"## {d['zh']} / {d['en']}  （{d['family']}，{why}）\n")
            f.write(f"- 本地 GUID：`{d['guids'][0] if d['guids'] else '?'}`\n")
            if not matched:
                f.write("- 该扩展下没有找到对应页面，需另找源或人工填写。\n\n")
                continue
            for name, fam, lv, rw, url in matched:
                f.write(f"  - [{fam}{' / ' + lv if lv else ''}] {rw}\n    {url}\n")
            f.write("\n")

    print(f"页面通过校验 {len(pages)} 个；唯一配对 {len(assigned)}；待确认 {len(ambig)}")


if __name__ == "__main__":
    main()
