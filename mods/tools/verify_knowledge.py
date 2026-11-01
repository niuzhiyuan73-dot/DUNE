"""知识层验收：结构、来源、预算，以及"按需机制"是否真的会命中。

用法：python -X utf8 -B mods/tools/verify_knowledge.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
KNOW = os.path.join(ROOT, "mods", "ai", "knowledge")
PASS, FAIL = [], []
SYSTEM_BUDGET = 9000


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print("  [%s] %s%s" % ("PASS" if ok else "FAIL", name, ("  -> " + detail) if detail else ""))


def read(p):
    # 知识文件可能被编辑器写成带 BOM 的 UTF-8；strip 掉，否则首行看起来不像注释
    return open(p, encoding="utf-8-sig").read() if os.path.exists(p) else ""


def main():
    print("=" * 70)
    print("知识层验收")
    print("=" * 70)

    core = read(os.path.join(KNOW, "core_rules.md"))
    mode = read(os.path.join(KNOW, "current_mode.md"))
    mech = read(os.path.join(KNOW, "mechanics.tsv"))
    exam = read(os.path.join(KNOW, "examples.tsv"))

    print("\n[1] 文件齐备")
    check("core_rules.md", bool(core), "%d 字" % len(core))
    check("current_mode.md", bool(mode), "%d 字" % len(mode))
    check("mechanics.tsv", bool(mech))
    check("examples.tsv", bool(exam))
    if FAIL:
        return finish()

    print("\n[2] 基础规则覆盖度")
    need = ["特使回合", "揭示回合", "战力", "平局", "造物主", "召回", "影响力", "同盟",
            "控制标记", "密谋", "招募点数", "游戏结束"]
    miss = [k for k in need if k not in core]
    check("关键机制均有章节", not miss, "缺: " + ", ".join(miss) if miss else "%d 项全覆盖" % len(need))
    check("写明了「规则非策略」的边界", "不写" in core and "策略" in core)
    check("含破平顺序", "香料" in core and "破平" in core)

    print("\n[3] 当前玩法关键覆盖")
    check("胜利阈值写明 16", "16" in mode)
    check("写明剧本优先于规则书", "以本文件为准" in mode or "优先" in mode)
    check("内容集 5 个都在", all(s in mode for s in ["基础", "伊克斯", "不朽", "崛起", "商会"]))
    check("冲突序列 10 场", mode.count("之战") >= 6 and "10" in mode)

    print("\n[4] 机制索引结构")
    rows, bad = [], []
    for i, t in enumerate(mech.splitlines(), 1):
        if not t.strip() or t.startswith("#"):
            continue
        c = t.split("\t")
        if len(c) != 6:
            bad.append("第%d行列数=%d" % (i, len(c)))
            continue
        keys = [k for k in c[2].split("|") if k.strip()]
        if not keys or any(len(k.strip()) < 2 for k in keys):
            bad.append("第%d行关键词过短" % i)
        if not c[5].strip():
            bad.append("第%d行缺来源" % i)
        rows.append(c)
    check("每行 6 列且关键词>=2字、有来源", not bad, "; ".join(bad[:3]) if bad else "%d 条" % len(rows))
    ids = [r[0] for r in rows]
    check("id 不重复", len(ids) == len(set(ids)))

    print("\n[5] 示例集结构")
    exs, ebad = [], []
    for i, t in enumerate(exam.splitlines(), 1):
        if not t.strip() or t.startswith("#"):
            continue
        c = t.split("\t")
        if len(c) != 5:
            ebad.append("第%d行列数=%d" % (i, len(c)))
            continue
        if not c[3].strip():
            ebad.append("第%d行缺正确判断" % i)
        exs.append(c)
    check("每行 5 列且含正确判断", not ebad, "; ".join(ebad[:3]) if ebad else "%d 条" % len(exs))
    eids = [r[0] for r in exs]
    check("id 不重复", len(eids) == len(set(eids)))

    print("\n[6] 提示词预算")
    system = core + mode + exam + "x" * 400
    check("系统提示在预算内（<= %d 字）" % SYSTEM_BUDGET, len(system) <= SYSTEM_BUDGET,
          "约 %d 字（含角色与示例渲染余量）" % len(system))

    print("\n[7] 按需机制命中")
    # (a) 构造样本：覆盖几类高价值机制，必须能命中 >=2 条
    probe = ("选择在航线上前进或召回你的运输船；随后获得一个技术谈判者；"
             "确认使用家族原子武器；战斗阶段打出战斗密谋。")
    ph = []
    for r in rows:
        for k in r[2].split("|"):
            if len(k.strip()) >= 2 and k.strip() in probe:
                ph.append("%s(%s)" % (r[1], k.strip()))
                break
    check("构造样本命中 >=2 条机制", len(ph) >= 2, ", ".join(ph[:4]) if ph else "一条都没命中")

    # (b) 真实日志命中率（仅报告，不做通过判据：真实选项常常是"放特使"，本就没有跨卡机制）
    import glob
    import json
    total = hit = 0
    for fn in sorted(glob.glob(os.path.join(ROOT, "dump", "llm", "coach_*.jsonl")))[-2:]:
        for line in open(fn, encoding="utf-8"):
            try:
                r = json.loads(line)
            except Exception:
                continue
            p = (r.get("pending") or "") + (r.get("state") or "")
            if len(p) < 200:
                continue
            total += 1
            if any(k.strip() in p for row_ in rows for k in row_[2].split("|") if len(k.strip()) >= 2):
                hit += 1
    if total:
        print("  [INFO] 真实待选列表命中率：%d/%d（%.0f%%）" % (hit, total, 100.0 * hit / total))

    finish()


def finish():
    print("\n" + "=" * 70)
    print("结果：PASS %d / FAIL %d" % (len(PASS), len(FAIL)))
    for f in FAIL:
        print("  FAILED: " + f)
    print("=" * 70)
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
