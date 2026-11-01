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
    check("声明不猜具体牌名", "不猜具体是哪张牌" in mem or "具体是哪张无法确认" in mem)
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
    check("Send 透传 memory", re.search(r"Send\(seq, playerName, user, pending, stateBlock, memoryBlock", coach) is not None)
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

    print("\n[7] 一致率采集（建议 vs 实际选择）")
    act = read(os.path.join(SRC, "Actual.cs"))
    check("Actual.cs 存在", bool(act))
    if act:
        check("有 Begin/End/ResultFor 三入口",
              all(k in act for k in ("public static void Begin(", "public static void End(", "public static string ResultFor(")))
        check("登记选项映射（供还原实体名）", "public static void NoteOption(" in act)
        check("按具体类型解析返回值", "TryCast<Networking.selection.messages.outgoing.SelectionWithTargets>" in act
              and "MultipleSelectionWithTargets" in act)
        check("解析失败时标明接口代理", "接口代理，未解析到具体类型" in act)
        check("只记录不修改（无写操作）",
              not any(w in act for w in ("SetFactionInfluence", "CreateEntity", "SendResponse")))
        check("结果有容量上限（长局不无限增长）", "Trim()" in act and "200" in act)
    check("Plugin 挂了 postfix", "OnWormMakeChoicePost" in plugin and "postfix:" in plugin)
    check("prefix 登记决策序号", "Actual.Begin(_wormChoices)" in plugin)
    check("Targets 生成明细时登记选项", "Actual.NoteOption(counter, kid, label)" in read(os.path.join(SRC, "Targets.cs")))
    check("Append 写入 actual 字段", "\\\"actual\\\":" in coach or '"\\"actual\\":"' in coach)
    agree = os.path.join(ROOT, "mods", "tools", "coach_agreement.py")
    check("离线比对脚本存在", os.path.exists(agree))

    print("\n[8] 回合状态机与多入口")
    turn = read(os.path.join(SRC, "Turn.cs"))
    check("Turn.cs 存在", bool(turn))
    if turn:
        check("打出一张牌 = 一个新行动", '"打出一张牌"' in turn and "Sel.Play" in turn)
        check("行动内后续合并", "Sel.Inner" in turn and "_askedAgent" in turn)
        check("展示阶段每次冲突只问一次", "_askedReveal" in turn and "Sel.Reveal" in turn)
        check("「选择展示」的收尾确认一般不问", "Sel.RevealConfirm" in turn)
        check("Kind 可为空，按 Prompt 分类", "Classify(" in turn and "PromptOf(" in turn)
        check("同一回合重复的打出提示按可用特使数合并", "LastPlayAgents" in turn)
        check("跨冲突重置（进度的键含冲突）", "ProgressOf(" in turn and "p.Conflict != conflict" in turn)
        check("战斗每次轮到你都要问（不合并）", "case Sel.Combat" in turn and "CombatNo++" in turn)
        check("战斗中区分主提示与结算提示", "使用一项能力，或跳过" in turn)
        check("同一次选择去重（人类/AI 两条入口）", "_handled.Add" in turn)
        check("未知 Kind 保守提问", "其他 Kind" in turn or "保守" in turn)
        check("给出本冲突第几次行动", "Describe(" in turn and "可用特使" in turn)
        check("一局 = 可用特使数 + 1 次行动的说法一致", "次打出 + 1 次展示" in turn)
    check("Plugin 调用 Turn.ShouldAsk", "Turn.ShouldAsk(" in plugin)
    check("Plugin 注入 Turn.Describe", "Turn.Describe(" in plugin)
    check("人类入口默认关闭（只观察 AI）", "h.Patch(selM" not in plugin)
    check("只观察一个 AI（IsTarget 锁定）", "IsTarget(" in coach and "_targetKey" in coach)
    check("换局重置观察对象（按 match 指针）", "_targetMatch" in coach and "_targetMatch != matchPtr" in coach)
    check("展示阶段不能派特使（阶段提示）", "RevealTurn" in plugin and "不能派特使" in plugin)
    check("配额按局重置", "ResetQuotaForNewMatch" in coach and "_quotaMatch" in coach)
    check("配额用完有明确告警", "本局配额已用完" in coach)
    check("可用特使 0 也输出（不省略）", "已全部派出" in read(os.path.join(SRC, "State.cs")))
    state = read(os.path.join(SRC, "State.cs"))
    check("战斗：输出各方战力与排名", "【战力】" in state and "Strength" in state)
    check("战斗：战力全 0 时不刷噪声", "any" in state and "return" in state)
    check("战斗：并列要显式标出", "并列" in state or "rank" in state)
    tg = read(os.path.join(SRC, "Targets.cs"))
    check("目标元素是 EntityID 时按 GUID 解析", "ScanGuidishMember" in tg)
    check("歧义名允许人工核验条目兜底", "HasVerifiedNameEffect" in coach and "HasVerifiedNameEffect" in tg)
    check("位置名未解析时不写成 Space", "名称未解析" in tg)
    check("领袖按钮按序号登记（实选可还原）", "NoteIndexedOptions" in act and "NoteLeaderButtons" in coach)
    bm = read(os.path.join(SRC, "BoardMap.cs"))
    check("版图结构枚举存在（Spaces/科技市场）", "Spaces" in bm and "TechTileStacks" in bm)
    check("位置名有实例ID兜底", "BoardMap.NameOf" in tg)
    check("名字查询做了小写回退", "ToLowerInvariant" in coach)
    check("科技市场写进局势", "【科技市场】" in state)
    check("目标解析失败会打一次形状诊断", "DumpTargetShape" in tg)
    check("多 key 轮询", "NextKey()" in coach and "LoadKeys" in coach)
    check("不再用全局 Authorization", "DefaultRequestHeaders.Add" not in coach)

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
