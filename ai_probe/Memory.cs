using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Canis.entities;
using worm.canis;

namespace DuneAiProbe
{
    /// <summary>
    /// 最小本局记忆：**只记已经发生的事实**，不把建议当成已执行。
    ///
    /// 为什么这么做：模型每次请求都是无状态调用（无会话、无历史），所以一局之内它会
    /// 反复自相矛盾（同一份冲突奖励给出两种解读）。而"记忆"必须先有事件流——
    /// 否则记下来的只是我们说过什么，不是局面发生过什么。
    ///
    /// 事件来源：同一玩家两次决策之间的快照差分（State.Capture）。
    /// 只覆盖可比较的标量：胜利点、四阵营影响力/同盟、手牌/牌库/密谋/弃牌数量、可用特使、当前冲突。
    ///
    /// 明确不做：
    ///   - 不猜"这张牌是哪张"（手牌每轮换，差集不可靠）→ 只在总数变化时记"牌组规模变化"并注明未知
    ///   - 不把上一轮建议写成已执行 → 建议单独分段并标注「未确认执行」
    ///   - 不跨局：以 match 指针为键，新局自动新建
    /// </summary>
    internal static class Memory
    {
        private const int MaxEvents = 8;     // 每人保留的最近事件
        private const int MaxAdvice = 3;     // 每人保留的最近建议
        private const int CharBudget = 520;  // 记忆块总字数上限（进 prompt，必须短）

        private sealed class Session
        {
            public long MatchPtr;
            public int Round;
            public readonly Dictionary<string, State.Snapshot> Last =
                new Dictionary<string, State.Snapshot>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<string>> Events =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public readonly Dictionary<string, List<string>> Advice =
                new Dictionary<string, List<string>>(StringComparer.Ordinal);
            public bool DiagLogged;
        }

        private static Session _s;
        private static readonly object _lock = new object();

        /// <summary>
        /// 在游戏线程调用：取快照 → 与上次比较 → 追加事件 → 返回可直接放进 prompt 的记忆块。
        /// 任何一步失败都返回 null（调用方按"没有记忆"处理），不影响决策主流程。
        /// </summary>
        public static string Update(Canis.Match match, PlayerEntity me, string playerName, ManualLogSource log)
        {
            try
            {
                if (me == null) return null;
                var snap = State.Capture(me);
                if (!snap.Valid) return null;

                string who = string.IsNullOrEmpty(playerName) ? "?" : playerName;
                long ptr = 0;
                try { ptr = match != null ? match.Pointer.ToInt64() : 0; } catch { }

                lock (_lock)
                {
                    if (_s == null || _s.MatchPtr != ptr)
                    {
                        _s = new Session { MatchPtr = ptr, Round = 1 };
                        if (log != null) log.LogInfo("[MEMORY] 新对局，记忆已重置 (match=" + ptr.ToString("x") + ")");
                    }

                    State.Snapshot prev;
                    bool hasPrev = _s.Last.TryGetValue(who, out prev) && prev.Valid;
                    var ev = GetList(_s.Events, who);

                    if (hasPrev) Diff(prev, snap, ev);
                    _s.Last[who] = snap;

                    // 首次渲染时打一条诊断：确认字段真的取到了，而不是全是默认值
                    if (!_s.DiagLogged && hasPrev)
                    {
                        _s.DiagLogged = true;
                        if (log != null)
                            log.LogInfo("[MEMORY] diag 首份记忆 who=" + who + " round=" + _s.Round
                                        + " vp=" + snap.Vp + " agents=" + snap.Agents
                                        + " conflict=" + (snap.Conflict ?? "?")
                                        + " events=" + ev.Count);
                    }

                    return Compose(who);
                }
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[MEMORY] update: " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        /// <summary>后台线程调用：把已经发出的建议记下来，明确标注"未确认执行"。</summary>
        public static void NoteAdvice(string playerName, string answer, ManualLogSource log)
        {
            try
            {
                if (string.IsNullOrEmpty(answer)) return;
                string who = string.IsNullOrEmpty(playerName) ? "?" : playerName;
                string one = FirstLine(answer);
                if (one.Length > 48) one = one.Substring(0, 48) + "…";

                lock (_lock)
                {
                    if (_s == null) return;
                    var list = GetList(_s.Advice, who);
                    // 带上轮次：让模型能判断"这是上一轮的建议，可能已经过时"
                    list.Add("第" + _s.Round + "轮：" + one);
                    while (list.Count > MaxAdvice) list.RemoveAt(0);
                }
            }
            catch (Exception e)
            {
                if (log != null) log.LogWarning("[MEMORY] advice: " + e.Message);
            }
        }

        /// <summary>显式清空（例如玩家手动开新局而 match 指针被复用时）。</summary>
        public static void Reset()
        {
            lock (_lock) { _s = null; }
        }

        // ---------- 差分 ----------

        private static void Diff(State.Snapshot a, State.Snapshot b, List<string> ev)
        {
            string r = "第" + (_s != null ? _s.Round : 1) + "轮";

            // 冲突换了 = 进入新一轮（新回合开始时会翻新的冲突牌）
            if (!string.IsNullOrEmpty(b.Conflict) && a.Conflict != b.Conflict && !string.IsNullOrEmpty(a.Conflict))
            {
                if (_s != null) _s.Round++;
                r = "第" + (_s != null ? _s.Round : 1) + "轮";
                ev.Add("【第" + (_s != null ? _s.Round : 1) + "轮】新冲突：" + b.Conflict);
            }

            if (a.Vp != b.Vp)
                ev.Add(r + "：胜利点 " + a.Vp + "→" + b.Vp + "（" + Sign(b.Vp - a.Vp) + "）");

            // 影响力：逐阵营报变化；涨到 2 是胜利点来源，值得点名
            for (int i = 0; i < 4 && i < b.Inf.Length; i++)
            {
                if (a.Inf[i] == b.Inf[i]) continue;
                string nm = State.FactionName(i);
                ev.Add(r + "：" + nm + "影响力 " + a.Inf[i] + "→" + b.Inf[i]
                       + (b.Inf[i] >= 2 && a.Inf[i] < 2 ? "（达到 2 点，应得 1 胜利点）" : ""));
            }

            for (int i = 0; i < 4 && i < b.Ally.Length; i++)
            {
                if (a.Ally[i] == b.Ally[i]) continue;
                ev.Add(r + (b.Ally[i] ? "：获得 " : "：失去 ") + State.FactionName(i) + "同盟");
            }

            // 牌组规模：不指名是哪张（手牌每轮换，差集不可靠），只报总数事实
            int ta = a.Hand + a.Deck + a.Discard, tb = b.Hand + b.Deck + b.Discard;
            if (ta != tb && ta > 0 && tb > 0)
                ev.Add(r + "：牌组总张数 " + ta + "→" + tb + "（具体是哪张无法确认）");

            if (b.Intrigue != a.Intrigue && b.Intrigue >= 0 && a.Intrigue >= 0)
                ev.Add(r + "：密谋手牌 " + a.Intrigue + "→" + b.Intrigue);

            if (b.AgentsKnown && a.AgentsKnown && b.Agents > a.Agents && b.Agents >= 3)
                ev.Add(r + "：特使已全部回收（可用 " + b.Agents + "）");

            while (ev.Count > MaxEvents) ev.RemoveAt(0);
        }

        // ---------- 组装 ----------

        private static string Compose(string who)
        {
            List<string> ev, ad;
            _s.Events.TryGetValue(who, out ev);
            _s.Advice.TryGetValue(who, out ad);

            bool hasEv = ev != null && ev.Count > 0;
            bool hasAd = ad != null && ad.Count > 0;
            if (!hasEv && !hasAd) return null;

            var sb = new StringBuilder();
            sb.Append("【本局记忆】（只含已确认发生的变化；建议只是建议，未必执行）\n");
            if (hasEv)
            {
                // 从最近往前写，保证超预算时丢掉的是最久远的
                for (int i = Math.Max(0, ev.Count - MaxEvents); i < ev.Count; i++)
                    sb.Append("- ").Append(ev[i]).Append('\n');
            }
            if (hasAd)
            {
                sb.Append("- 曾建议（未确认执行）：");
                for (int i = 0; i < ad.Count; i++)
                {
                    if (i > 0) sb.Append("；");
                    sb.Append(ad[i]);
                }
                sb.Append('\n');
            }

            string text = sb.ToString();
            if (text.Length > CharBudget)
            {
                // 超预算时保留后半段（越靠后越新）
                text = text.Substring(text.Length - CharBudget);
                int nl = text.IndexOf('\n');
                if (nl > 0 && nl < text.Length - 1) text = text.Substring(nl + 1);
                text = "【本局记忆】（较早的已省略）\n" + text;
            }
            return text;
        }

        // ---------- 小工具 ----------

        private static List<string> GetList(Dictionary<string, List<string>> d, string k)
        {
            List<string> v;
            if (!d.TryGetValue(k, out v)) { v = new List<string>(); d[k] = v; }
            return v;
        }

        private static string Sign(int d)
        {
            return d > 0 ? "+" + d : d.ToString();
        }

        private static string FirstLine(string s)
        {
            string t = s.Replace("**", "").Trim();
            int nl = t.IndexOfAny(new[] { '\n', '\r' });
            if (nl > 0) t = t.Substring(0, nl);
            return t.Trim();
        }
    }
}
