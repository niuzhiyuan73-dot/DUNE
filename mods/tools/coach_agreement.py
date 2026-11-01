"""一致率统计：教练建议 vs AI 实际选择。

数据来源：dump/llm/coach_*.jsonl 的 actual 字段（由插件 postfix 记录 MakeChoice 返回值）。
判定方式刻意保守：
  - 从 pending 里抽出候选名（形如 `- 渗透(2f18c4c4)〔…〕`）
  - 答案里"最先出现的候选名"视为它建议的对象
  - actual 文本里出现的候选名视为 AI 实际选的对象
  - 两边都定位到才计入分母；定位不到记 unknown，单独统计
  - 同名多实例（渗透×2）视为同一项——它们本来就没有区别

用法：
  python -X utf8 -B mods/tools/coach_agreement.py
  python -X utf8 -B mods/tools/coach_agreement.py --show 10   # 看前 10 条不一致样本
  python -X utf8 -B mods/tools/coach_agreement.py --unknown    # 只看没定位到的 actual 原文
"""
import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
LLM = os.path.join(ROOT, "dump", "llm")

NAME_RE = re.compile(r"^-\s*([^（(〔\n]+?)\s*(?:[（(][0-9a-f]{8}[）)])?\s*(?:〔|：|$)", re.M)


def load():
    rows = []
    for fn in sorted(glob.glob(os.path.join(LLM, "coach_*.jsonl"))):
        for line in open(fn, encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except Exception:
                continue
            r["_file"] = os.path.basename(fn)
            rows.append(r)
    return rows


def candidates(pending):
    out = []
    for m in NAME_RE.finditer(pending or ""):
        nm = m.group(1).strip()
        if not nm or nm.startswith("〔") or len(nm) > 24:
            continue
        if nm not in out:
            out.append(nm)
    return out


def first_hit(text, names):
    """返回文本中最先出现的候选名（同位置取更长者，避免「渗透」吃掉「渗透行动」）。"""
    if not text:
        return None
    best, pos = None, 1 << 30
    for nm in names:
        i = text.find(nm)
        if i < 0:
            continue
        if i < pos or (i == pos and best and len(nm) > len(best)):
            best, pos = nm, i
    return best


def main():
    argv = sys.argv[1:]
    show = 10
    if "--show" in argv:
        show = int(argv[argv.index("--show") + 1])
    only_unknown = "--unknown" in argv

    rows = load()
    with_actual = [r for r in rows if (r.get("actual") or "").strip()]
    print("=" * 72)
    print("教练建议 vs AI 实际选择 · 一致率")
    print("  日志总条数 %d，其中带 actual 的 %d 条" % (len(rows), len(with_actual)))
    if not with_actual:
        print("\n  还没有 actual 数据：需要跑一局（build 2026-11-01t 起才有该字段）。")
        print("  插件 postfix 会把 MakeChoice 的返回值写进 jsonl 的 actual 字段。")
        print("=" * 72)
        return 0

    both = same = 0
    unk_actual = []
    unk_answer = []
    diffs = []

    for r in with_actual:
        names = candidates(r.get("pending") or "")
        if not names:
            continue
        a = first_hit(r.get("answer") or "", names)
        b = first_hit(r.get("actual") or "", names)
        if not b:
            unk_actual.append(r)
            continue
        if not a:
            unk_answer.append(r)
            continue
        both += 1
        if a == b:
            same += 1
        else:
            diffs.append((r, a, b))

    if only_unknown:
        print("\n[actual 未定位到候选名的样本]")
        for r in unk_actual[:show]:
            print("  - %s #%s %s" % (r.get("_file"), r.get("seq"), (r.get("actual") or "")[:100]))
        print("\n[答案未定位到候选名的样本]")
        for r in unk_answer[:show]:
            print("  - %s #%s %s" % (r.get("_file"), r.get("seq"), (r.get("answer") or "")[:80]))
        return 0

    print("\n可判定 %d 条：一致 %d，不一致 %d" % (both, same, both - same))
    if both:
        print("一致率 %.0f%%" % (100.0 * same / both))
    print("无法判定：actual 未定位 %d 条 / 答案未定位 %d 条" % (len(unk_actual), len(unk_answer)))

    if diffs:
        print("\n[不一致样本]（建议 → 实际）")
        for r, a, b in diffs[:show]:
            print("  - %s #%s %s：建议「%s」／实际「%s」" % (r.get("_file"), r.get("seq"),
                                                          r.get("player", "")[:12], a, b))
    if unk_actual:
        print("\n[actual 原文样例，用于改进匹配]")
        for r in unk_actual[:5]:
            print("  - %s" % (r.get("actual") or "")[:160])

    print("\n" + "=" * 72)
    print("说明：一致率高不等于建议正确——它只说明教练与 Hard 档 AI 权重同向；")
    print("      不一致也不等于建议错，可能是 AI 的启发式在当前局面失效。")
    print("=" * 72)
    return 0


if __name__ == "__main__":
    sys.exit(main())
