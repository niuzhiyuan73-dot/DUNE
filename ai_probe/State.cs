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
                    sb.Append('\n');
                }

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

        private static string SafeStr(PlayerEntity p)
        {
            try { return p.PlayerName ?? "?"; } catch { return "?"; }
        }
    }
}
