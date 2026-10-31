using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using Canis.attributes;
using Canis.entities;
using worm.canis.entities;

namespace DuneAiProbe
{
    /// <summary>
    /// 局面渲染器：把"当前局势"压成几行中文，拼进 prompt。
    ///
    /// 属性名不能靠猜，所以这里对每个候选名 try 一遍：
    /// 成功的写进局势块，失败的记一次日志——第一次运行就能把真实属性名试出来。
    /// </summary>
    internal static class State
    {
        private static readonly string[] VpNames = { "VictoryPoints", "VictoryPoint", "Score", "VPs" };
        private static readonly string[] ResNames = { "Solari", "Spice", "Water", "Troops", "Garrison", "Influence" };

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
                log.LogInfo("[STATE] players=" + n);

                sb.Append("【局势】\n");
                for (int i = 0; i < n; i++)
                {
                    PlayerEntity p = null;
                    try { p = players[i]; } catch { }
                    if (p == null) continue;

                    string nm = Safe(() => p.PlayerName);
                    bool isAI = false;
                    try { isAI = p.IsAI; } catch { }
                    bool isMe = false;
                    try { isMe = p.Pointer == asker.Pointer; } catch { }

                    string vp = FirstInt(p, VpNames);
                    var res = new List<string>();
                    foreach (var r in ResNames)
                    {
                        string v = TryInt(p, r);
                        if (v != null) res.Add(r + " " + v);
                    }

                    sb.Append("- ").Append(nm ?? "?").Append(isMe ? "（本 AI）" : (isAI ? "（AI）" : "（玩家）"));
                    if (vp != null) sb.Append("｜VP ").Append(vp);
                    if (res.Count > 0) sb.Append("｜").Append(string.Join("，", res));
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

        private static string FirstInt(PlayerEntity p, string[] names)
        {
            foreach (var n in names)
            {
                string v = TryInt(p, n);
                if (v != null) return v;
            }
            return null;
        }

        private static string TryInt(PlayerEntity p, string attrName)
        {
            try
            {
                var def = AttributeDefinitions.GetDefinition<int>(attrName);
                if (def == null) return null;
                var a = p.GetAttribute(def);
                if (a == null) return null;
                return a.Value.ToString();
            }
            catch { return null; }
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }
    }
}
