using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Canis.entities;
using worm.canis.entities;

namespace DuneAiProbe
{
    /// <summary>
    /// 局面渲染器：把"当前局势"压成几行中文，拼进 prompt。
    ///
    /// 取值全部走游戏自带的 WormEntityExtensions（VictoryPoints / Solari / Spice / Water / Troops），
    /// 不再靠属性名字符串去猜——那些名字在 AttributeDefinitions 里对不上。
    /// </summary>
    internal static class State
    {
        private static bool _probed;

        public static string Render(PlayerEntity asker, ManualLogSource log)
        {
            var sb = new StringBuilder();
            try
            {
                var wp = asker != null ? asker.TryCast<WormPlayer>() : null;
                if (wp == null) { log.LogInfo("[STATE] asker is not WormPlayer"); return null; }

                var match = wp.match;
                if (match == null) { log.LogInfo("[STATE] no match"); return null; }

                var players = match.players;
                if (players == null) { log.LogInfo("[STATE] no players"); return null; }

                int n = players.Count;
                sb.Append("【局势】\n");
                for (int i = 0; i < n; i++)
                {
                    PlayerEntity p = null;
                    try { p = players[i]; } catch { }
                    if (p == null) continue;

                    string nm = SafeStr(p);
                    bool isAI = false;
                    try { isAI = p.IsAI; } catch { }
                    bool isMe = false;
                    try { isMe = asker != null && p.Pointer == asker.Pointer; } catch { }

                    sb.Append("- ").Append(nm)
                      .Append(isMe ? "（本 AI）" : (isAI ? "（AI）" : "（玩家）"));

                    TryAppend(sb, "VP", () => WormEntityExtensions.VictoryPoints(p));
                    TryAppend(sb, "太阳币", () => WormEntityExtensions.Solari(p));
                    TryAppend(sb, "香料", () => WormEntityExtensions.Spice(p));
                    TryAppend(sb, "水", () => WormEntityExtensions.Water(p));
                    TryAppend(sb, "兵", () => WormEntityExtensions.Troops(p));

                    // 静态卡牌规则之外，模型还需要局面信息才能判断好坏。
                    // 下面这些用反射按属性名只读取：取不到就跳过，**绝不猜数值** ——
                    // 错的数字比没有数字更糟，模型会把错数据当依据。
                    TryAppendReflect(sb, "可用工人", p, "AvailableAgents", "Workers");
                    TryAppendReflect(sb, "手牌", p, "HandCards", "HandSize");
                    TryAppendReflect(sb, "影响力", p, "InfluenceValue", "Influence");
                    TryAppendReflect(sb, "影响力等级", p, "InfluenceRank");
                    sb.Append('\n');
                }

                // 当前冲突与市场是全局状态，不是每个玩家各自的，单独列一行
                string shared = RenderShared(match);
                if (!string.IsNullOrEmpty(shared)) sb.Append(shared);

                if (!_probed)
                {
                    _probed = true;
                    log.LogInfo("[STATE] first render:\n" + sb);
                }
                return sb.ToString();
            }
            catch (Exception e)
            {
                log.LogWarning("[STATE] " + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static void TryAppend(StringBuilder sb, string label, Func<int> get)
        {
            try { sb.Append("｜").Append(label).Append(' ').Append(get()); }
            catch { }
        }

        /// <summary>
        /// 按属性名反射读取一个只读属性并输出。
        ///
        /// 局面信息里有些数值（可用工人、手牌、影响力、市场）没有稳定的强类型入口，
        /// 这里按名字试。**取不到就完全不输出** —— 局面数据宁缺毋错：
        /// 错的数字会被模型当成事实依据，比没有这个字段危害大得多。
        /// </summary>
        private static void TryAppendReflect(StringBuilder sb, string label, object obj, params string[] propNames)
        {
            for (int i = 0; i < propNames.Length; i++)
            {
                try
                {
                    var pi = obj.GetType().GetProperty(propNames[i],
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.FlattenHierarchy);
                    if (pi == null) continue;
                    var v = pi.GetValue(obj);
                    if (v == null) continue;

                    // 集合 -> 元素个数；数字 -> 直接用
                    var cnt = v as System.Collections.ICollection;
                    if (cnt != null) { sb.Append("｜").Append(label).Append(' ').Append(cnt.Count); return; }

                    if (v is int iv) { sb.Append("｜").Append(label).Append(' ').Append(iv); return; }

                    var s = v.ToString();
                    if (!string.IsNullOrEmpty(s) && s.Length < 40)
                    {
                        sb.Append("｜").Append(label).Append(' ').Append(s);
                        return;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 全局局面：当前冲突（等级/奖励）与市场状态。
        ///
        /// 当前冲突的奖励是模型判断"要不要打这场冲突"的关键。
        /// 取不到时整行不输出，不做任何推断。
        /// </summary>
        private static string RenderShared(Canis.Match match)
        {
            try
            {
                var sb = new StringBuilder();
                TryAppendReflect(sb, "当前冲突", match, "CurrentConflict");
                TryAppendReflect(sb, "市场机会", match, "MarketOpportunity");
                TryAppendReflect(sb, "市场可买", match, "MarketOpportunityBuy");
                TryAppendReflect(sb, "市场可卖", match, "MarketOpportunitySell");
                if (sb.Length == 0) return null;
                return sb.ToString() + "\n";
            }
            catch { return null; }
        }

        private static string SafeStr(PlayerEntity p)
        {
            try { return p.PlayerName ?? "?"; } catch { return "?"; }
        }
    }
}
