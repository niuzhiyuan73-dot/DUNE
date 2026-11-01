"""把「卡牌 -> 效果能力」诊断结果翻译成卡牌效果文本。

背景：本地化里 314 组卡牌有 106 组只有标题（基础包与部分扩展是图标化表达），
但这些卡的效果在游戏里由**能力实体**实现，而 `worm.canis.abilities.<类名>`
在本地化里有官方中文（380 条中 338 条有文案）。

插件侧 `ai_probe/CardDiag.cs` 负责导出 dump/ai/abilities.txt：
    entityName <TAB> 原型GUID <TAB> 中文名 <TAB> 能力名1|能力名2|...

本脚本把它拼成效果文本，追加到 mods/ai/kb_extra.tsv（中文名作键，已有序目不覆盖）。

用法：
    python mods/tools/join_abilities.py            # 只报告
    python mods/tools/join_abilities.py --write    # 写入 kb_extra.tsv
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "mods", "tools"))
import build_name_map as B

DIAG = os.path.join(ROOT, "dump", "ai", "abilities.txt")
KB = os.path.join(ROOT, "mods", "ai", "kb_extra.tsv")
EFFECT = os.path.join(ROOT, "mods", "ai", "effect_map.tsv")

MAX_ABIL = 4          # 每张牌最多拼几个能力
MAX_LEN = 90          # 与 effect_map 的长度约定一致
PFX = "worm.canis.abilities."


def clean(text):
    """去掉富文本标签与占位符。"""
    if not text:
        return ""
    t = re.sub(r"<sprite\s+name=\"?([^\">]+)\"?\s*/?>", r"\1", text, flags=re.I)
    t = re.sub(r"</?[a-z][^>]*>", "", t, flags=re.I)
    t = t.replace("{0}", "N").replace("[name]", "")
    t = re.sub(r"\s+", "", t)
    return t.strip(" 　;；。")


def has_effect(name, eff_map):
    return ("name:" + name) in eff_map


def main():
    write = "--write" in sys.argv
    if not os.path.exists(DIAG):
        sys.exit("缺少 dump/ai/abilities.txt —— 先跑一局让插件产出（日志里搜 CARDDIAG）")

    zh = B.load_locale()
    abil_text = {k[len(PFX):]: v for k, v in zh.items() if k.startswith(PFX)}

    eff_map = {}
    for line in io.open(EFFECT, encoding="utf-8"):
        k, _, v = line.rstrip("\n").partition("\t")
        eff_map[k] = v

    kb_keys = set()
    for line in io.open(KB, encoding="utf-8"):
        if line.strip() and not line.startswith("#"):
            kb_keys.add(line.split("\t")[0].strip())

    rows, stats = [], {"total": 0, "have": 0, "built": 0, "no_abil": 0, "no_text": 0}
    for line in io.open(DIAG, encoding="utf-8"):
        line = line.rstrip("\r\n")
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 4:
            continue
        stats["total"] += 1
        en_name, arch, zh_name, abil = parts[0], parts[1], parts[2], parts[3]
        if not zh_name:
            continue
        if has_effect(zh_name, eff_map) or zh_name in kb_keys:
            stats["have"] += 1
            continue
        names = [a for a in abil.split("|") if a]
        if not names:
            stats["no_abil"] += 1
            continue

        texts = []
        for a in names[:MAX_ABIL]:
            t = clean(abil_text.get(a, ""))
            if t and t not in texts:
                texts.append(t)
        if not texts:
            stats["no_text"] += 1
            continue

        text = "；".join(texts)
        if len(text) > MAX_LEN:
            text = text[:MAX_LEN] + "…"
        rows.append((zh_name, text, "游戏内能力实体 " + ",".join(names[:2])))
        stats["built"] += 1

    print(f"诊断条目 {stats['total']}：已有文本 {stats['have']}、"
          f"可拼出 {stats['built']}、无能力 {stats['no_abil']}、能力无文案 {stats['no_text']}")
    for r in rows[:12]:
        print(f"  {r[0]:10s} {r[1][:64]}")

    if write and rows:
        s = io.open(KB, encoding="utf-8").read()
        if not s.endswith("\n"):
            s += "\n"
        s += "\n# 卡牌效果：由游戏内能力实体拼出（本地化 worm.canis.abilities.*，官方中文）。\n"
        s += "# 来源：插件 CardDiag 导出的 dump/ai/abilities.txt。能力参数（如“最多N个”）无法取值，以 N 占位。\n"
        s += "\n".join(f"{n}\t{t}\t{src}" for n, t, src in rows) + "\n"
        io.open(KB, "w", encoding="utf-8", newline="\n").write(s)
        print(f"\n已写入 {len(rows)} 条到 {KB}；接着跑 python mods/tools/build_effect_map.py")


if __name__ == "__main__":
    main()
