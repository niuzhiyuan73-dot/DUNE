"""从官方中文规则书的「版图位置速查表」抽取空间效果。

来源（docs/pdf/，官方 PDF，简体中文）：
  SCN_DUNE_IMPERIUM_Rulebook_24-10-22.pdf          基础包 22 个空间
  SCN_DUNE_IMPERIUM_UPRISING_Rules_Supplements.pdf 起义 26 个空间
  SCN_DUNE_IMPERIUM_RISE_OF_IX_Rulebook*.pdf       伊克斯（若有）

为什么用规则书而不是截图/诊断：官方中文、逐空间成条、术语与游戏一致。

条目格式（PDF 文本层）：
  厄拉奇恩（Arrakeen）
  特使图标：城市
  战斗位置
  费用：2 水资源
  <效果若干行>

输出：dump/pdf_txt/spaces_pdf.tsv  image<TAB>中英名<TAB>图标<TAB>战场<TAB>费用<TAB>效果
用法：python mods/tools/parse_space_sheets.py
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "mods", "tools"))
import build_name_map as B

TXT = os.path.join(ROOT, "dump", "pdf_txt")
OUT = os.path.join(ROOT, "dump", "pdf_txt", "spaces_pdf.tsv")

# 中文名（English）  或  中文名换行（English）
HEAD = re.compile(r"^(.+?)\s*（([A-Za-z][A-Za-z' ]+)）\s*$")


def norm_en(s):
    # PDF 里 Spice Reﬁnery 用的是 fi 连字（U+FB01），直接 lower 会变成 "reﬁnery" 而错过匹配
    s = s.replace("\ufb01", "fi").replace("\ufb02", "fl").replace("\ufb00", "ff")
    return re.sub(r"[^a-z0-9]", "", s.lower())


def join_split_heads(lines):
    """把被排版拆成两行的标题合并：\n最高委员会\n（High Council） -> 最高委员会（High Council）。

    不合并的话，「最高委员会」会被当成上一条（远航机）的效果正文吞进去。
    """
    out = []
    for l in lines:
        if l.startswith("（") and l.endswith("）") and len(l) > 4 and out and re.match(r"^[A-Za-z][A-Za-z' ]+$", l[1:-1]):
            out[-1] = out[-1] + l
        else:
            out.append(l)
    return out


def collect_entries(text):
    lines = join_split_heads([l.strip() for l in text.split("\n")])
    entries, cur = [], None
    for i, l in enumerate(lines):
        m = HEAD.match(l)
        if m and not l.startswith(("特使", "费用")):
            if cur:
                entries.append(cur)
            cur = {"name_zh": m.group(1).strip(), "name_en": m.group(2).strip(),
                   "icon": "", "battle": "", "cost": "", "effect": []}
            continue
        if cur is None:
            continue
        if l.startswith("特使图标"):
            cur["icon"] = l.split("：", 1)[-1].strip()
        elif l == "战斗位置":
            cur["battle"] = "是"
        elif l.startswith("费用"):
            cur["cost"] = l.split("：", 1)[-1].strip()
        elif l.startswith("需求"):
            cur["effect"].append(l)
        elif l and not re.match(r"^\d+$", l) and "版图位置速查表" not in l and "每个版图位置" not in l:
            cur["effect"].append(l)
    if cur:
        entries.append(cur)
    return entries


def main():
    en = B.load_locale("en_US")
    en2img = {}
    for k, v in en.items():
        m = re.match(r"^worm\.space\.([a-z0-9_]+)$", k)
        if m:
            en2img.setdefault(norm_en(v), []).append(m.group(1))

    # 只取「版图位置速查表」所在的两份（官方简体中文）：
    #   基础规则书末尾 + 起义补充规则。不朽/Ix 规则书里没有速查表，
    #   散落的说明会解析出残缺条目（数字是图形，文本层没有），必须排除。
    SOURCES = ("SCN_DUNE_IMPERIUM_Rulebook_24-10-22.txt",
               "SCN_DUNE_IMPERIUM_UPRISING_Rules_Supplements.txt")
    rows, unmatched = [], []
    for f in SOURCES:
        if not os.path.exists(os.path.join(TXT, f)):
            print("缺少", f)
            continue
        text = io.open(os.path.join(TXT, f), encoding="utf-8", errors="replace").read()
        # PDF 里 Spice Reﬁnery / Reﬁ nery 用的是 fi 连字（U+FB01），不是 ASCII，
        # 会让下面的英文名正则（[A-Za-z]）匹配失败。统一归一化后再解析。
        for lig, plain in (("\ufb01", "fi"), ("\ufb02", "fl"), ("\ufb00", "ff"), ("\ufb03", "ffi")):
            text = text.replace(lig, plain)
        ents = collect_entries(text)
        hit = 0
        for e in ents:
            key = norm_en(e["name_en"])
            imgs = en2img.get(key)
            if not imgs:
                unmatched.append((f, e["name_zh"], e["name_en"]))
                continue
            hit += 1
            eff = " ".join(x for x in e["effect"] if x).strip()
            eff = re.sub(r"\s+", "", eff)
            # 数字缺失是硬伤（PDF 里数字可能被拆行/图形化）：出现「抽张牌」这类就报警
            if re.search(r"[抽征兵获唤部派遣费用需求选择支付]\D{0,3}(张牌|次|点|水资源|香料|宇宙索|部队)", eff) \
                    and not re.search(r"\d", eff):
                print(f"   [warn 缺数字] {e['name_zh']}: {eff[:50]}")
            for img in imgs:
                rows.append((img, e["name_zh"], e["name_en"], e["icon"], e["battle"], e["cost"], eff))
        print(f"{f[:52]:54s} 解析 {len(ents):2d} 条，对上 {hit}")

    seen, uniq = set(), []
    for r in rows:
        if r[0] in seen:
            continue
        seen.add(r[0])
        uniq.append(r)

    with open(OUT, "w", encoding="utf-8", newline="\n") as fo:
        fo.write("image\t中文名\t英文名\t特使图标\t战斗位置\t费用\t效果\n")
        for r in uniq:
            fo.write("\t".join(r) + "\n")
    print(f"\n写出 {len(uniq)} 个空间 -> {OUT}")
    if unmatched:
        print(f"未匹配 {len(unmatched)} 条（规则书里有、游戏里没找到对应键）：")
        for u in unmatched[:12]:
            print("   ", u)


if __name__ == "__main__":
    main()
