using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace DuneAiProbe
{
    /// <summary>
    /// 回合状态机：决定「这一次选择要不要问教练」。
    ///
    /// 为什么需要它：游戏的决策粒度是"一个选择点"，而玩家的心智单位是"一个回合"。
    /// 一个回合 = 1 次 PlayerTurn（打出一张牌进入特使/展示）+ 之后若干次 AgentTurn
    /// （打密谋、选目标、派兵、抽牌…）。实测今天 78 个回合起点配了 213 次回合内追问，
    /// 平均每回合 2.7 问，每问只看到一步 —— 所以拿到的是碎片，不是回合计划。
    ///
    /// 现在的规则：
    ///   PlayerTurn  → 问（新回合开始，让它出整回合行动链）
    ///   AgentTurn   → 同回合内已问过则**不再问**（避免碎片）
    ///   其他/未知   → 保守地问（宁可多问一次，也不要漏掉战斗阶段等路径）
    ///
    /// 去重键是「玩家对象 + selection 的 counter」：人类与 AI 两条入口可能对同一次选择
    /// 各触发一次（SelectFrom 与 MakeChoice），必须只问一次。
    /// </summary>
    internal static class Turn
    {
        private static readonly object _lock = new object();

        // 已处理过的选择：playerKey + counter
        private static readonly HashSet<string> _handled = new HashSet<string>(StringComparer.Ordinal);

        // 每个玩家当前回合是否已经问过
        private static readonly HashSet<long> _askedThisTurn = new HashSet<long>();

        private static int _asked, _skipped;
        private static ManualLogSource _log;

        private static readonly Regex KindRe = new Regex(@"Kind\s+(\w+)", RegexOptions.Compiled);

        public static void Init(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>返回 true 表示这次选择应当请求教练。</summary>
        public static bool ShouldAsk(long playerKey, int counter, string pending, ManualLogSource log)
        {
            try
            {
                string kind = KindOf(pending);
                string id = playerKey.ToString("x") + "#" + counter;

                lock (_lock)
                {
                    if (counter > 0 && !_handled.Add(id))
                        return false;   // 同一次选择已经被另一条入口处理过

                    if (kind == "AgentTurn")
                    {
                        if (_askedThisTurn.Contains(playerKey))
                        {
                            _skipped++;
                            if (log != null && _skipped % 5 == 1)
                                log.LogInfo("[TURN] 同阶段追问已合并（累计跳过 " + _skipped + " 次，已问 " + _asked + " 次）");
                            return false;
                        }
                        return true;   // 回合首问不是 PlayerTurn 的少见情形，照问
                    }

                    // PlayerTurn / RevealTurn 都是"阶段起点"：开新阶段并标记已问，
                    // 否则同阶段的后续 AgentTurn 会继续追问（上一版就是漏了 Add）。
                    if (kind == "PlayerTurn" || kind == "RevealTurn")
                    {
                        _askedThisTurn.Remove(playerKey);
                        _askedThisTurn.Add(playerKey);
                        _asked++;
                        return true;
                    }

                    if (kind == "AssignLeader")
                    {
                        // 开局指派领袖：每个玩家只问一次，不问也不算回合
                        if (_askedThisTurn.Contains(playerKey)) return false;
                        _askedThisTurn.Add(playerKey);
                        _asked++;
                        return true;
                    }

                    // 其他 Kind（含未来的战斗入口）：保守地问，不压制
                    _asked++;
                    return true;
                }
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[TURN] " + e.Message);
                return true;   // 出错时宁可问
            }
        }

        /// <summary>标记本回合已提问（PlayerTurn 分支已做，这里供外部显式同步）。</summary>
        public static void MarkAsked(long playerKey)
        {
            lock (_lock) { _askedThisTurn.Add(playerKey); }
        }

        public static void Reset()
        {
            lock (_lock) { _handled.Clear(); _askedThisTurn.Clear(); _asked = 0; _skipped = 0; }
        }

        public static string Stat()
        {
            lock (_lock) return "asked=" + _asked + " merged=" + _skipped;
        }

        private static string KindOf(string pending)
        {
            if (string.IsNullOrEmpty(pending)) return "";
            var m = KindRe.Match(pending);
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
