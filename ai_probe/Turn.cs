using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using BepInEx.Logging;

namespace DuneAiProbe
{
    /// <summary>
    /// 回合状态机：决定「这一次选择要不要问教练」，并给出「现在打到第几个行动」。
    ///
    /// 术语（按游戏内实际含义，2026-11-01 校正）：
    ///   行动（回合）= 打出一张牌 + 它带来的全部后续（选位置、付费用、位置效果、打密谋、派兵、抽牌）
    ///   一次冲突     = 每个玩家「可用特使数」次行动 + 最后 1 次展示行动
    ///                  例：3 个特使 → 3 次打出 + 1 次展示 = 4 个回合
    ///   一局         = 10 次冲突（本玩法冲突牌库固定 10 张，不重洗）
    ///
    /// 为什么按「行动」而不是按「冲突」提问：一个行动内部是先打牌再选位置再结算，
    /// 拆开问就是碎片；而行动之间局面已经变了，必须重新问。
    ///
    /// 提问规则：
    ///   打出一张牌（PlayerTurn, Prompt 含「打出一张牌」）→ 问，开一个新行动
    ///   选择「展示」来进入展示阶段（PlayerTurn 的收尾确认）      → 不问，只有一个选项
    ///   展示阶段（RevealTurn，含连续买牌）                      → 本次冲突只问一次，后续合并
    ///   行动内的 AgentTurn                                     → 已问过则合并
    ///   指派领袖（AssignLeader）                                → 每个玩家一次
    ///   其它 Kind（含未来战斗入口）                              → 保守地问
    /// </summary>
    internal static class Turn
    {
        private static readonly object _lock = new object();

        // 已处理过的选择：playerKey + counter（人类与 AI 两条入口可能各触发一次）
        private static readonly HashSet<string> _handled = new HashSet<string>(StringComparer.Ordinal);

        // 当前行动内已经问过（打牌后到展示前）
        private static readonly HashSet<long> _askedAgent = new HashSet<long>();
        // 本次冲突的展示阶段已经问过
        private static readonly HashSet<long> _askedReveal = new HashSet<long>();
        // 指派领袖已经问过
        private static readonly HashSet<long> _askedLeader = new HashSet<long>();

        private class Progress
        {
            public string Conflict;
            public int PlayNo;      // 本冲突第几次打出
            public int Agents;      // 本冲突的可用特使数（第 1 次打出时记录）
            public int CombatNo;    // 本冲突战斗阶段第几次轮到我
            public int LastPlayAgents = -1;   // 上一次「打出」提示时的可用特使数（-1 = 本冲突还没问过）
        }

        private static readonly Dictionary<long, Progress> _prog = new Dictionary<long, Progress>();


        private static int _asked, _skipped;
        private static ManualLogSource _log;

        private static readonly Regex KindRe = new Regex(@"Kind\s+(\w+)", RegexOptions.Compiled);
        private static readonly Regex PromptRe = new Regex(@"Prompt\s+(.+?)。", RegexOptions.Compiled);
        private static readonly Regex ConflictRe = new Regex(@"【当前冲突】([^:：（(\r\n]+)", RegexOptions.Compiled);
        private static readonly Regex AgentRe = new Regex(@"可用特使\s*(\d+)", RegexOptions.Compiled);

        public static void Init(ManualLogSource log)
        {
            _log = log;
        }

        /// <summary>选择点的性质。Kind 在部分选择上是空串（实测「放置一名间谍」等），所以按 Prompt 判。</summary>
        private enum Sel { Play, RevealConfirm, Reveal, Combat, Leader, Inner }

        /// <summary>返回 true 表示这次选择应当请求教练。</summary>
        public static bool ShouldAsk(long playerKey, int counter, string pending, string stateBlock, ManualLogSource log)
        {
            try
            {
                Sel sel = Classify(pending);
                string id = playerKey.ToString("x") + "#" + counter;

                lock (_lock)
                {
                    if (counter > 0 && !_handled.Add(id))
                        return false;   // 同一次选择已经被另一条入口处理过

                    switch (sel)
                    {
                        case Sel.Play:
                        {
                            // 判「是不是新回合」不看提示本身：同一回合内先打一张阴谋牌，
                            // 游戏会再弹一次「打出一张牌」（实测 #24/#25 同秒两条），
                            // 唯一可靠的判据是**可用特使数有没有减少**。
                            Progress pg = ProgressOf(playerKey, stateBlock, out bool f1);
                            int now = AgentsOf(stateBlock);
                            bool repeat = pg.LastPlayAgents >= 0 && now >= 0 && now >= pg.LastPlayAgents;
                            pg.LastPlayAgents = now;
                            if (repeat) { Merge(log, "同一回合内又弹出打出提示（未消耗特使）", pending); return false; }

                            _askedAgent.Add(playerKey);
                            _askedReveal.Remove(playerKey);
                            _asked++;
                            return true;
                        }

                        case Sel.RevealConfirm:
                            // 「选择展示来进入展示阶段」：只有一个选项，问它没意义。
                            // 但手里有魁萨茨·哈德拉克时这一步是真选择（确认即放弃额外回合）。
                            // 只看 Prompt：游戏在哈德拉克可用时会把提示改成
                            // 「你依然可以进行一次特使阶段（受魁萨茨·哈德拉克影响）」。
                            // 不能搜整段 pending —— 公共牌池里也可能有这张牌，会误判。
                            if (PromptOf(pending).IndexOf("魁萨茨·哈德拉克", StringComparison.Ordinal) < 0)
                            { Merge(log, "进入展示阶段的确认", pending); return false; }
                            _asked++;
                            return true;

                        case Sel.Reveal:
                            // 展示阶段（含连续买牌）：每次冲突只问一次
                            if (_askedReveal.Contains(playerKey)) { Merge(log, "展示阶段内连续买牌", pending); return false; }
                            _askedReveal.Add(playerKey);
                            _asked++;
                            return true;

                        case Sel.Combat:
                        {
                            // 战斗阶段每步都必须单独问：轮到你时「打哪张 / 打几张 / 跳过」都要按当前战力重算。
                            // 规则上"跳过不算退出"，所以每一次轮到你都是新的决定，绝不能合并。
                            Progress pg = ProgressOf(playerKey, stateBlock, out bool fresh);
                            pg.CombatNo++;
                            _asked++;
                            return true;
                        }

                        case Sel.Leader:
                            if (_askedLeader.Contains(playerKey)) return false;
                            _askedLeader.Add(playerKey);
                            _asked++;
                            return true;

                        default:
                            // 行动内的后续（放置间谍、选阵营、确认召回…）：本回合已问过就合并。
                            // 若本回合还没问过（例如中途接入），照问一次，不要整段丢掉。
                            if (_askedAgent.Contains(playerKey)) { Merge(log, "行动内后续操作", pending); return false; }
                            _asked++;
                            return true;
                    }
                }
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[TURN] " + e.Message);
                return true;   // 出错时宁可问
            }
        }

        /// <summary>按 Kind + Prompt 判定选择点性质。Kind 可能为空串，Prompt 才是稳定的判据。</summary>
        private static Sel Classify(string pending)
        {
            string kind = KindOf(pending);
            string prompt = PromptOf(pending);

            // 战斗阶段两种提示都算战斗：主提示「打出一张密牌或过牌」、
            // 卡牌结算时「使用一项能力，或跳过」（本地化 worm.combat.intrigue[.resolution]）。
            if (kind == "Combat"
                || prompt.IndexOf("打出一张密牌", StringComparison.Ordinal) >= 0
                || prompt.IndexOf("使用一项能力，或跳过", StringComparison.Ordinal) >= 0)
                return Sel.Combat;
            if (prompt.IndexOf("打出一张牌", StringComparison.Ordinal) >= 0)
                return Sel.Play;
            if (prompt.IndexOf("进入展示阶段", StringComparison.Ordinal) >= 0)
                return Sel.RevealConfirm;
            if (kind == "RevealTurn"
                || prompt.IndexOf("使用一项能力或获取一张牌", StringComparison.Ordinal) >= 0
                || prompt.IndexOf("选择一项能力来使用", StringComparison.Ordinal) >= 0)
                return Sel.Reveal;
            if (kind == "AssignLeader") return Sel.Leader;
            if (kind == "PlayerTurn") return Sel.Play;      // 兜底：Kind 有值但 Prompt 没匹配上
            return Sel.Inner;
        }

        /// <summary>取（或新建）本玩家在当前冲突里的进度；换冲突自动重置。</summary>
        private static Progress ProgressOf(long playerKey, string stateBlock, out bool fresh)
        {
            string conflict = ConflictOf(stateBlock);
            Progress p;
            fresh = false;
            if (!_prog.TryGetValue(playerKey, out p) || p.Conflict != conflict)
            {
                int remain = AgentsOf(stateBlock);
                p = new Progress
                {
                    Conflict = conflict,
                    PlayNo = 0,
                    Agents = remain > 0 ? remain : 0,
                    CombatNo = 0
                };
                _prog[playerKey] = p;
                fresh = true;
                if (_prog.Count > 64) _prog.Clear();
            }
            return p;
        }

        /// <summary>
        /// 给出「现在是本冲突的第几个行动」。
        /// 必须在渲染出 stateBlock 之后调用（要知道冲突名与该玩家的可用特使数）。
        /// </summary>
        public static string Describe(long playerKey, string pending, string stateBlock)
        {
            try
            {
                Sel sel = Classify(pending);
                string conflict = ConflictOf(stateBlock);
                int remain = AgentsOf(stateBlock);

                lock (_lock)
                {
                    Progress p;
                    if (!_prog.TryGetValue(playerKey, out p) || p.Conflict != conflict)
                    {
                        p = new Progress
                        {
                            Conflict = conflict,
                            PlayNo = 0,
                            // 冲突开头可用特使数 = 本冲突的行动数（展示回合另算一次）
                            Agents = remain > 0 ? remain : 0
                        };
                        _prog[playerKey] = p;
                        if (_prog.Count > 64) _prog.Clear();   // 防长跑累积
                    }

                    if (sel == Sel.Reveal)
                    {
                        int total = p.Agents > 0 ? p.Agents + 1 : p.PlayNo + 1;
                        return "【本回合】本冲突第 " + total + " 次行动 = **展示回合**（本冲突共 " +
                               (p.Agents > 0 ? p.Agents + " 次打出 + 1 次展示" : "若干次打出 + 1 次展示") +
                               "，这是最后一次）。\n" +
                               "展示阶段能做的只有：执行手牌的展示效果、花说服购买卡牌。**不能派特使**，" +
                               "渗透/家族原子武器这类只在派特使时才有用的牌一律进「保留」。\n" +
                               "如果本阶段可以连续买多张，按顺序写进同一份计划，并注明每张付多少说服、买完还剩多少（剩余点数轮末作废）。\n" +
                               "可用说服直接看【局势】里的「说服 N」——**没出现这个字段就是 0**，不要写「未知」。";
                    }

                    if (sel == Sel.Play || sel == Sel.RevealConfirm)
                    {
                        if (sel == Sel.RevealConfirm)
                            return "【本回合】进入展示阶段前的最后一次机会。可用特使已全部派出（0 个），" +
                                   "但手里有**魁萨茨·哈德拉克**：打出它可以在 0 特使时再派 1 名特使，等于多出一个行动。\n" +
                                   "要么用掉它再打一个行动，要么确认进入展示（本次冲突的最后一个行动）。" +
                                   "其余牌此时只能打密谋或能力，不能再派特使。";

                        p.PlayNo++;
                        string pos;
                        if (p.Agents > 0)
                        {
                            pos = "本冲突第 " + p.PlayNo + " 次打出（可用特使 " + p.Agents +
                                  " 个，正常共 " + p.Agents + " 次打出 + 1 次展示 = " + (p.Agents + 1) + " 个回合）";
                            int left = p.Agents - p.PlayNo;
                            if (left > 0)
                                pos += "；这次之后还剩 " + left + " 次打出 + 1 次展示";
                            else if (left == 0)
                                pos += "；这是本冲突最后一次打出，之后还剩 1 次展示";
                            else
                                pos += "；已超出可用特使数——这是**魁萨茨·哈德拉克**这类牌提供的额外派遣" +
                                       "（它允许在 0 特使时再派 1 名特使），不是正常回合，不要据此以为还能继续派";
                        }
                        else
                        {
                            pos = "本冲突第 " + p.PlayNo + " 次打出（可用特使已为 0 或未取到）";
                        }
                        return "【本回合】" + pos + "。\n" +
                               "**这次行动 = 打出 1 张牌 + 它带来的全部后续**（选位置、付费用、结算位置效果、打密谋、派兵、抽牌），" +
                               "全部写在这一份计划里；不需要为下一步另做打算，也不要安排两次特使行动。";
                    }

                    if (sel == Sel.Combat)
                    {
                        int k = p.CombatNo > 0 ? p.CombatNo : 1;
                        return "【战斗】本冲突战斗阶段，第 " + k + " 次轮到你。\n"
                             + "每次轮到你都可以：打出 **1 张或多张战斗密谋**，或者**跳过**。"
                             + "「跳过」是界面上的独立按钮，不在待选列表里也算合法。\n"
                             + "**跳过不是退出**：之后再次轮到你仍可打；只有**所有玩家连续跳过**才会结算。\n"
                             + "判断方法：看【战力】那一行的本方数值与名次 → 打出后能否改变名次、换到第几栏奖励；"
                             + "**改变不了名次就别浪费牌**（比如已经稳拿第二、且第一追不上）。"
                             + "只有战斗密谋能在这里打，阴谋牌与终局密谋都不行。";
                    }

                    if (sel == Sel.Leader)
                        return "【本回合】开局指派领袖，只做一次选择，没有行动链。";

                    return null;   // 行动内后续：不加额外说明，合并后不需要
                }
            }
            catch (Exception e)
            {
                if (_log != null) _log.LogWarning("[TURN] describe: " + e.Message);
            }
            return null;
        }

        public static void Reset()
        {
            lock (_lock)
            {
                _handled.Clear(); _askedAgent.Clear(); _askedReveal.Clear(); _askedLeader.Clear();
                _prog.Clear(); _asked = 0; _skipped = 0;
            }
        }

        public static string Stat()
        {
            lock (_lock) return "asked=" + _asked + " merged=" + _skipped;
        }

        // 合并过的 Prompt 首句（去重，只记首次）：万一将来出现没识别的新阶段，日志里能看见
        private static readonly HashSet<string> _mergedPrompts = new HashSet<string>(StringComparer.Ordinal);

        private static void Merge(ManualLogSource log, string what, string pending)
        {
            _skipped++;
            if (log != null && _skipped % 3 == 1)
                log.LogInfo("[TURN] 已合并「" + what + "」（累计合并 " + _skipped + " 次，已问 " + _asked + " 次）");

            if (log == null || _mergedPrompts.Count >= 40) return;
            string pr = PromptOf(pending);
            if (string.IsNullOrEmpty(pr) || !_mergedPrompts.Add(pr)) return;
            log.LogInfo("[TURN] 合并过的新 Prompt：「" + pr + "」");
        }

        /// <summary>打出一张牌 = 真行动；「选择展示来进入展示阶段」= 收尾确认。</summary>
        private static bool IsRevealConfirm(string pending)
        {
            if (string.IsNullOrEmpty(pending)) return false;
            string prompt = PromptOf(pending);
            bool announcesReveal = prompt.IndexOf("进入展示阶段", StringComparison.Ordinal) >= 0;
            bool isPlay = prompt.IndexOf("打出一张牌", StringComparison.Ordinal) >= 0;
            return announcesReveal && !isPlay;
        }

        private static string PromptOf(string pending)
        {
            if (string.IsNullOrEmpty(pending)) return "";
            var m = PromptRe.Match(pending);
            return m.Success ? m.Groups[1].Value : "";
        }

        private static string ConflictOf(string stateBlock)
        {
            if (string.IsNullOrEmpty(stateBlock)) return "";
            var m = ConflictRe.Match(stateBlock);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        /// <summary>取「本 AI」那一行的可用特使数；人类玩家回落为「玩家」行。读不到返回 -1。</summary>
        private static int AgentsOf(string stateBlock)
        {
            if (string.IsNullOrEmpty(stateBlock)) return -1;
            string line = null;
            foreach (var raw in stateBlock.Split('\n'))
            {
                if (raw.IndexOf("（本 AI）", StringComparison.Ordinal) >= 0) { line = raw; break; }
                if (raw.IndexOf("（玩家）", StringComparison.Ordinal) >= 0) line = raw;
            }
            if (line == null) return -1;
            var m = AgentRe.Match(line);
            int v;
            return m.Success && int.TryParse(m.Groups[1].Value, out v) ? v : -1;
        }

        private static string KindOf(string pending)
        {
            if (string.IsNullOrEmpty(pending)) return "";
            var m = KindRe.Match(pending);
            return m.Success ? m.Groups[1].Value : "";
        }
    }
}
