"""本局记忆的契约验收（源码级）。

为什么做源码检查而不是跑起来测：记忆依赖运行时局面对象，离线无法构造；
但"链路是否接对"是可以静态确认的——历史上就出现过 relay 参数漏传、
记忆块被插到错误位置这类问题，靠人工看代码容易漏。

检查四件事：
  1) 记忆模块的硬约束（预算、上限、措辞）还在；
  2) 快照字段取不到时有"未知"标记，不把读不到当成 0；
  3) 链路：Plugin -> Memory.Update -> Coach.Observe -> Send -> Append -> jsonl；
  4) 提示词段落顺序：局势 → 本局记忆 → 相关机制 → 待选列表。

用法：python -X utf8 -B mods/tools/verify_memory.py
"""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SRC = os.path.join(ROOT, "ai_probe")
PASS, FAIL = [], []


def read(p):
    return open(p, encoding="utf-8-sig").read() if os.path.exists(p) else ""


def check(name, ok, detail=""):
    (PASS if ok else FAIL).append(name)
    print("  [%s] %s%s" % ("PASS" if ok else "FAIL", name, ("  -> " + detail) if detail else ""))


def main():
    mem = read(os.path.join(SRC, "Memory.cs"))
    state = read(os.path.join(SRC, "State.cs"))
    coach = read(os.path.join(SRC, "Coach.cs"))
    plugin = read(os.path.join(SRC, "Plugin.cs"))

    print("=" * 70)
    print("本局记忆 · 契约验收")
    print("=" * 70)

    print("\n[1] 文件与硬约束")
    check("Memory.cs 存在", bool(mem), "%d 字节" % len(mem))
    if not mem:
        return finish()
    for name, pat, why in [
        ("事件上限 MaxEvents", r"MaxEvents\s*=\s*(\d+)", "保留最近 N 条"),
        ("建议上限 MaxAdvice", r"MaxAdvice\s*=\s*(\d+)", "保留最近 N 条建议"),
        ("字符预算 CharBudget", r"CharBudget\s*=\s*(\d+)", "记忆块进 prompt 的上限"),
    ]:
        m = re.search(pat, mem)
        check(name + " 已定义", bool(m), ("= " + m.group(1)) if m else "未找到")
    m = re.search(r"CharBudget\s*=\s*(\d+)", mem)
    if m:
        check("字符预算不超过 800", int(m.group(1)) <= 800, m.group(1) + " 字")

    print("\n[2] 语义约束（只记事实，不把建议当执行）")
    check("明确声明只记已确认变化", "只含已确认发生的变化" in mem)
    check("建议标注为未确认执行", "未确认执行" in mem)
    check("建议带轮次前缀", '"第" + _s.Round + "轮："' in mem)
    check("声明不猜具体牌名", "具体是哪张无法确认" in mem)
    check("失败返回 null（不阻断决策）",
          re.search(r"catch\s*\(Exception e\)[\s\S]{0,220}?return null;", mem) is not None)

    print("\n[3] 快照取值（读不到要标记未知）")
    check("State 定义 Snapshot", "internal struct Snapshot" in state)
    check("有 Capture 入口", "internal static Snapshot Capture" in state)
    check("可用特使未知时标记", "AgentsKnown" in state and "public bool AgentsKnown" in state)
    check("冲突名单独取值（判断换轮）", "ConflictNameOf" in state)
    check("弃牌数单独取值", "CountDiscard" in state)

    print("\n[4] 链路完整性")
    check("Plugin 调用 Memory.Update", "Memory.Update(" in plugin)
    check("Plugin 把记忆传给 Observe", re.search(r"Coach\.Observe\([^;]*memoryBlock", plugin) is not None)
    check("Observe 接收 memoryBlock 参数", "string memoryBlock" in coach)
    check("记忆块拼进 user 消息", 'user += memoryBlock' in coach)
    check("Send 透传 memory", re.search(r"Send\(seq, playerName, user, pending, stateBlock, memoryBlock\)", coach) is not None)
    check("Append 写入 memory 字段", '"\\"memory\\":"' in coach or "\\\"memory\\\":" in coach)
    check("Append 记录建议", "Memory.NoteAdvice(" in coach)

    print("\n[5] 提示词段落顺序")
    i_state = coach.find('stateBlock + "\\n"')
    i_mem = coach.find("user += memoryBlock")
    i_mech = coach.find("【相关机制】")
    i_pend = coach.find("待选列表：")
    ok_order = -1 < i_state < i_mem < i_mech < i_pend
    check("顺序为 局势 → 记忆 → 机制 → 待选", ok_order,
          "位置 state=%d memory=%d mech=%d pending=%d" % (i_state, i_mem, i_mech, i_pend))

    print("\n[6] 不越界（不改游戏状态）")
    # 记忆只应读，不应出现写操作入口
    bad = [w for w in ("SetFactionInfluence", "AddVictoryPoints", "CreateEntity", "SetAttribute") if w in mem]
    check("Memory.cs 无写操作调用", not bad, ", ".join(bad) if bad else "仅调用 Capture 与日志")

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
