# -*- coding: utf-8 -*-
"""回合粒度体检：对照日志检查「每次冲突问了几个回合」。

判定标准（2026-11-01 与用户对齐）：
  一个回合 = 打出 1 张牌 + 它带来的全部后续
  一次冲突 = 可用特使数 次打出 + 1 次展示

所以每次冲突应当恰好：
  「打出」提问数 ≈ 该冲突的可用特使数（含效果给的额外派遣，可能 +1）
  「展示」提问数 = 1
  「选择展示来进入展示阶段」的收尾确认 = 0 次提问
  「行动内后续操作」提问 = 0 次（应被合并）

用法：
  python -X utf8 -B mods/tools/turn_check.py                  # 今天的日志
  python -X utf8 -B mods/tools/turn_check.py --since "2026-11-01 20:00"
"""
import argparse
import collections
import glob
import json
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def load(since=None):
    rows = []
    for fn in sorted(glob.glob(os.path.join(ROOT, "dump", "llm", "coach_*.jsonl"))):
        for line in open(fn, encoding="utf-8"):
            line = line.strip()
            if not line:
                continue
            try:
                r = json.loads(line)
            except Exception:
                continue
            if since and (r.get("ts") or "") < since:
                continue
            r["_file"] = os.path.basename(fn)
            rows.append(r)
    return rows


def counter_of(r):
    m = re.search(r"Counter (\d+)", r.get("pending") or "")
    return int(m.group(1)) if m else 10 ** 9


def kind_of(p):
    m = re.search(r"Kind (\w+)", p or "")
    return m.group(1) if m else "?"


def prompt_of(p):
    m = re.search(r"Prompt (.+?)。", p or "")
    return m.group(1) if m else ""


def is_reveal_confirm(p):
    t = prompt_of(p)
    return "进入展示阶段" in t and "打出一张牌" not in t


def conflict_of(state):
    m = re.search(r"【当前冲突】([^:：（(\r\n]+)", state or "")
    return m.group(1).strip() if m else "?"


def agents_of(state):
    """优先「本 AI」行；人类玩家才回落「玩家」行。
    注意：人类那行不一定有「可用特使」字段，先命中就返回会误判成 None。"""
    lines = (state or "").split("\n")
    for tag in ("（本 AI）", "（玩家）"):
        for line in lines:
            if tag in line:
                m = re.search(r"可用特使\s*(\d+)", line)
                if m:
                    return int(m.group(1))
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--since", default=None)
    args = ap.parse_args()

    rows = [r for r in load(args.since) if kind_of(r.get("pending")) in
            ("PlayerTurn", "RevealTurn", "AgentTurn", "AssignLeader")]
    if not rows:
        print("没有可用样本")
        return 1
    rows.sort(key=lambda r: (r.get("ts") or "", counter_of(r)))

    # 按（玩家, 冲突）分组
    groups = collections.OrderedDict()
    for r in rows:
        key = (r.get("player"), conflict_of(r.get("state")))
        groups.setdefault(key, []).append(r)

    print("玩家 / 冲突                   打出  展示  确认  行动内  可用特使  结论")
    print("-" * 84)
    total = {"play": 0, "reveal": 0, "confirm": 0, "inner": 0}
    bad = 0
    for (player, conflict), rs in groups.items():
        if conflict in ("?", ""):
            continue
        play = sum(1 for r in rs if kind_of(r.get("pending")) == "PlayerTurn"
                   and not is_reveal_confirm(r.get("pending")))
        confirm = sum(1 for r in rs if kind_of(r.get("pending")) == "PlayerTurn"
                      and is_reveal_confirm(r.get("pending")))
        reveal = sum(1 for r in rs if kind_of(r.get("pending")) == "RevealTurn")
        inner = sum(1 for r in rs if kind_of(r.get("pending")) == "AgentTurn")
        # 可用特使取该冲突内「打出」提问的最大读数（第一条打出时尚未派出，正常就是最大值；
        # 取 max 是为了容忍中途接入或读数缺一次的偶发情况）
        vals = [agents_of(r.get("state")) for r in rs
                if kind_of(r.get("pending")) == "PlayerTurn" and not is_reveal_confirm(r.get("pending"))]
        vals = [v for v in vals if v is not None]
        agents = max(vals) if vals else None

        verdict = []
        if confirm:
            verdict.append("确认被问了 %d 次" % confirm)
        if inner:
            verdict.append("行动内被问 %d 次" % inner)
        if reveal > 1:
            verdict.append("展示问了 %d 次" % reveal)
        if agents is not None and play not in (agents, agents + 1):
            verdict.append("打出 %d 次 ≠ 特使 %d" % (play, agents))
        ok = not verdict
        if not ok:
            bad += 1
        print("%-14s %-12s  %4d  %4d  %4d  %6d  %8s  %s" % (
            (player or "?")[:14], conflict[:12], play, reveal, confirm, inner,
            "-" if agents is None else agents, "OK" if ok else "；".join(verdict)))
        total["play"] += play
        total["reveal"] += reveal
        total["confirm"] += confirm
        total["inner"] += inner

    n_conf = sum(1 for k in groups if k[1] not in ("?", ""))
    print("-" * 84)
    print("冲突段 %d 个｜提问合计 %d（打出 %d + 展示 %d + 确认 %d + 行动内 %d）" % (
        n_conf, total["play"] + total["reveal"] + total["confirm"] + total["inner"],
        total["play"], total["reveal"], total["confirm"], total["inner"]))
    print("期望：确认 = 0，行动内 = 0，展示 ≤ 1/冲突，打出 = 可用特使数（±1 为效果额外派遣）")
    print("结论：%s" % ("全部符合" if bad == 0 else "%d 个冲突段不符合" % bad))
    return 0 if bad == 0 else 2


if __name__ == "__main__":
    sys.exit(main())
