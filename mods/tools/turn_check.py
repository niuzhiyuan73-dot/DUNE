# -*- coding: utf-8 -*-
"""回合粒度体检：复刻 Turn.cs 的判定，离线算出「每次冲突实际会问几个回合」。

判定标准（2026-11-01 与用户对齐）：
  一个回合 = 打出 1 张牌 + 它带来的全部后续
  一次冲突 = 可用特使数 次打出 + 1 次展示
  唯一例外 = 魁萨茨·哈德拉克（移动已有特使，0 特使时也能打出，多出一个回合）

复刻的规则（与 ai_probe/Turn.cs 一一对应）：
  打出提示      → 只有「可用特使数比上次打出时更少」才算新回合；否则是同一回合内
                  先打了阴谋牌又弹出来的同一条提示，合并
  进入展示确认  → 不问；仅当 Prompt 里点名哈德拉克时问（此时是真选择）
  展示阶段      → 每次冲突只问一次，后续连续买牌合并
  行动内后续    → 合并（放置间谍 / 选阵营 / 确认召回…）
  战斗          → 每步都问（不合并）

用法：
  python -X utf8 -B mods/tools/turn_check.py
  python -X utf8 -B mods/tools/turn_check.py --since "2026-11-01 20:25"
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
    return m.group(1) if m else ""


def prompt_of(p):
    m = re.search(r"Prompt (.+?)。", p or "")
    return m.group(1) if m else ""


def classify(p):
    """与 Turn.cs 的 Classify 一致：Kind 可能为空串，Prompt 才是稳定判据。"""
    k, t = kind_of(p), prompt_of(p)
    if k == "Combat" or "打出一张密牌" in t:
        return "combat"
    if "打出一张牌" in t:
        return "play"
    if "进入展示阶段" in t:
        return "confirm"
    if k == "RevealTurn" or "使用一项能力或获取一张牌" in t or "选择一项能力来使用" in t:
        return "reveal"
    if k == "AssignLeader":
        return "leader"
    return "inner"


def conflict_of(state):
    m = re.search(r"【当前冲突】([^:：（(\r\n]+)", state or "")
    return m.group(1).strip() if m else "?"


def agents_of(state):
    """优先「本 AI」行，人类回落「玩家」行（人类那行可能没有这个字段）。"""
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

    rows = sorted([r for r in load(args.since) if r.get("pending")],
                  key=lambda r: (r.get("ts") or "", counter_of(r)))

    last_play = {}
    asked_inner = set()
    asked_reveal = set()
    asked_leader = set()

    asks = []
    merged = collections.Counter()
    seen = set()

    for r in rows:
        p, who = r.get("pending") or "", r.get("player")
        sel = classify(p)
        key = (who, counter_of(r))
        if key in seen:
            merged["重复入口"] += 1
            continue
        seen.add(key)
        cf = conflict_of(r.get("state"))

        if sel == "play":
            now = agents_of(r.get("state"))
            prev = last_play.get(who)
            last_play[who] = now
            if prev is not None and now is not None and now >= prev:
                merged["同一回合又弹出打出提示"] += 1
                continue
            asked_inner.add(who)
            asked_reveal.discard(who)
            asks.append((who, cf, "play"))
        elif sel == "confirm":
            if "魁萨茨·哈德拉克" not in prompt_of(p):
                merged["进入展示的确认"] += 1
                continue
            asks.append((who, cf, "confirm"))
        elif sel == "reveal":
            if who in asked_reveal:
                merged["展示阶段内连续买牌"] += 1
                continue
            asked_reveal.add(who)
            asks.append((who, cf, "reveal"))
        elif sel == "combat":
            asks.append((who, cf, "combat"))
        elif sel == "leader":
            if who in asked_leader:
                merged["重复指派领袖"] += 1
                continue
            asked_leader.add(who)
            asks.append((who, cf, "leader"))
        else:
            if who in asked_inner:
                merged["行动内后续"] += 1
                continue
            asks.append((who, cf, "inner"))

    groups = collections.OrderedDict()
    for who, cf, sel in asks:
        groups.setdefault((who, cf), collections.Counter())[sel] += 1

    agent_by_conf = {}
    for r in rows:
        cf = conflict_of(r.get("state"))
        if cf in ("?", ""):
            continue
        if classify(r.get("pending")) == "play":
            a = agents_of(r.get("state"))
            if a is not None:
                agent_by_conf[cf] = max(agent_by_conf.get(cf, 0), a)

    print("玩家 / 冲突                   打出  展示  确认  行动内  战斗  可用特使  结论")
    print("-" * 92)
    total = collections.Counter()
    bad = 0
    for (who, cf), c in groups.items():
        if cf in ("?", ""):
            continue
        agents = agent_by_conf.get(cf)
        verdict = []
        if c["inner"]:
            verdict.append("行动内被问 %d 次" % c["inner"])
        if c["reveal"] > 1:
            verdict.append("展示问了 %d 次" % c["reveal"])
        if c["confirm"] > 1:
            verdict.append("确认问了 %d 次" % c["confirm"])
        if agents is not None and c["play"] not in (agents, agents + 1):
            verdict.append("打出 %d 次 ≠ 特使 %d" % (c["play"], agents))
        ok = not verdict
        bad += 0 if ok else 1
        print("%-14s %-12s  %4d  %4d  %4d  %6d  %4d  %8s  %s" % (
            (who or "?")[:14], cf[:12], c["play"], c["reveal"], c["confirm"],
            c["inner"], c["combat"], "-" if agents is None else agents,
            "OK" if ok else "；".join(verdict)))
        total.update(c)

    print("-" * 92)
    print("冲突段 %d 个｜会提问 %d 条（打出 %d + 展示 %d + 确认 %d + 行动内 %d + 战斗 %d）" % (
        sum(1 for k in groups if k[1] not in ("?", "")), sum(total.values()),
        total["play"], total["reveal"], total["confirm"], total["inner"], total["combat"]))
    if merged:
        print("已合并：" + "、".join("%s %d" % (k, v) for k, v in merged.most_common()))
    print("期望：行动内 = 0，展示 ≤ 1/冲突，确认仅当 Prompt 点名哈德拉克，"
          "打出 = 可用特使数（+1 为哈德拉克这类额外派遣）")
    print("结论：%s" % ("全部符合" if bad == 0 else "%d 个冲突段不符合" % bad))
    return 0 if bad == 0 else 2


if __name__ == "__main__":
    sys.exit(main())
