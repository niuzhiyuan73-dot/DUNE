using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Logging;
using Canis.entities;
using Canis.utils.ids;
using worm.canis.data.enums;
using worm.canis.entities;
using worm.canis;

namespace DuneAiProbe
{
    /// <summary>
    /// 局面渲染器：把"当前局势"压成几行中文，拼进 prompt。
    ///
    /// 全部走游戏自带接口（dump/api_worm_canis.txt 有据可查）：
    ///   WormEntityExtensions.VictoryPoints/Solari/Spice/Water/Troops —— 资源
    ///   WormPlayer.GetFactionInfluence(Factions) / InAlliance(Factions) —— 四阵营影响力与同盟
    ///   WormPlayer.ConflictTroops/GarrisonTroops/ConflictDreadnoughts/GarrisonDreadnoughts —— 兵力分类
    ///   WormPlayer.HandCount/DeckCount/IntrigueHandCount/HandCards()/IntrigueHand —— 牌区
    ///   WormBoard.CurrentConflict / WormSpace.HasAgent/Agents/ControllingPlayer —— 棋盘
    /// 取不到就整段跳过：**绝不猜数值** —— 错的数字比没有数字更糟。
    /// </summary>
    internal static class State
    {
        private static bool _probed;

        private static readonly string[] FactionNames = { "皇帝", "宇航公会", "姐妹会", "弗雷曼" };
        private static readonly Factions[] FactionIds =
            { Factions.Emperor, Factions.SpacingGuild, Factions.BeneGesserit, Factions.Fremen };

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

                    AppendTroopBreakdown(sb, p);
                    AppendFactions(sb, p);
                    var wp2 = p.TryCast<WormPlayer>();
                    if (wp2 != null)
                    {
                        AppendRemainingAgents(sb, wp2, log);
                        TryAppend(sb, "手牌", () => wp2.HandCount);
                        TryAppend(sb, "牌库", () => wp2.DeckCount);
                        TryAppend(sb, "密谋", () => wp2.IntrigueHandCount);
                    }
                    AppendDiscardCount(sb, p);
                    sb.Append('\n');

                    // 明细只给本 AI：对手手牌构成是隐藏信息，数量即可。
                    if (isMe && wp2 != null)
                    {
                        string hand = EntityListNames(wp2.HandCards());
                        if (!string.IsNullOrEmpty(hand))
                            sb.Append("  手牌明细：").Append(hand).Append('\n');
                        string intr = EntityListNames(GetIntrigueHand(p));
                        if (!string.IsNullOrEmpty(intr))
                            sb.Append("  密谋明细：").Append(intr).Append('\n');
                    }
                }

                // 全局局面：当前冲突 + 版图占用
                string shared = RenderShared(match, log);
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

        // ---------- 每玩家分段 ----------

        /// <summary>兵营/战场/无畏舰 分类兵力（接口见 api_worm_canis.txt:1407-1414）。</summary>
        private static void AppendTroopBreakdown(StringBuilder sb, PlayerEntity p)
        {
            var wp = p.TryCast<WormPlayer>();
            if (wp == null) return;
            try
            {
                int ct = wp.ConflictTroops, gt = wp.GarrisonTroops;
                int cd = wp.ConflictDreadnoughts, gd = wp.GarrisonDreadnoughts;
                var parts = new List<string>();
                if (ct > 0 || gt > 0) parts.Add("部队 战场" + ct + "/兵营" + gt);
                if (cd > 0 || gd > 0) parts.Add("无畏舰 战场" + cd + "/兵营" + gd);
                if (parts.Count > 0)
                    sb.Append("｜兵力分类 ").Append(string.Join("，", parts.ToArray()));
            }
            catch { }
        }

        /// <summary>四阵营影响力 + 同盟（GetFactionInfluence / InAlliance，T_wormplayer2.txt:313-315）。</summary>
        private static void AppendFactions(StringBuilder sb, PlayerEntity p)
        {
            var wp = p.TryCast<WormPlayer>();
            if (wp == null) return;
            try
            {
                var parts = new List<string>();
                var allies = new List<string>();
                for (int i = 0; i < FactionIds.Length; i++)
                {
                    try
                    {
                        int v = wp.GetFactionInfluence(FactionIds[i]);
                        parts.Add(FactionNames[i] + v);
                    }
                    catch { }
                    try
                    {
                        if (wp.InAlliance(FactionIds[i])) allies.Add(FactionNames[i]);
                    }
                    catch { }
                }
                if (parts.Count > 0)
                    sb.Append("｜影响力 ").Append(string.Join(" ", parts.ToArray()));
                if (allies.Count > 0)
                    sb.Append("｜同盟 ").Append(string.Join("、", allies.ToArray()));
            }
            catch { }
        }

        /// <summary>弃牌堆数量：Discard 是 WormPlayArea（Entity 容器），数量取子实体数。</summary>
        private static void AppendDiscardCount(StringBuilder sb, PlayerEntity p)
        {
            try
            {
                var wp = p.TryCast<WormPlayer>();
                if (wp == null) return;
                var area = wp.Discard;
                if (area == null) return;

                var cnt = area as System.Collections.ICollection;
                if (cnt != null) { sb.Append("｜弃牌 ").Append(cnt.Count); return; }

                var e = area.TryCast<Entity>();
                if (e != null)
                {
                    var ch = e.Children;
                    if (ch != null) { sb.Append("｜弃牌 ").Append(ch.Count); return; }
                }

                int c = Il2CppCount(area);
                if (c >= 0) sb.Append("｜弃牌 ").Append(c);
            }
            catch { }
        }

        /// <summary>
        /// 密谋手牌：get_IntrigueHand 返回 WormPlayArea（Entity 容器），
        /// 堆里的牌就是它的子实体（同 SpaceDiag 的 Children 模式）。
        /// </summary>
        private static System.Collections.IEnumerable GetIntrigueHand(PlayerEntity p)
        {
            try
            {
                var wp = p.TryCast<WormPlayer>();
                if (wp == null) return null;
                var area = wp.IntrigueHand;
                if (area == null) return null;
                var e = area.TryCast<Entity>();
                if (e == null) return null;
                var ch = e.Children;
                if (ch == null) return null;
                var res = new List<Entity>();
                int n = ch.Count;
                for (int i = 0; i < n; i++)
                {
                    try { var c = ch[i]; if (c != null) res.Add(c); } catch { }
                }
                return res;
            }
            catch { return null; }
        }

        /// <summary>实体集合 -> "名字×2、名字"，名字经 name_map（原型 GUID）转中文。
        /// 兼容托管 IEnumerable 与 Il2Cpp 非托管枚举两条路径。</summary>
        private static string EntityListNames(object entsObj)
        {
            if (entsObj == null) return null;
            var counts = new List<string>();
            var order = new List<string>();
            var tally = new Dictionary<string, int>(StringComparer.Ordinal);
            int total = 0;

            void AddOne(object o)
            {
                var e = o as Entity;
                if (e == null && o is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                    e = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)o).TryCast<Entity>();
                if (e == null) return;
                total++;
                string nm = NameOfEntity(e);
                if (string.IsNullOrEmpty(nm)) nm = "?";
                if (!tally.ContainsKey(nm)) { tally[nm] = 1; order.Add(nm); }
                else tally[nm]++;
            }

            try
            {
                if (entsObj is System.Collections.IEnumerable men && !(entsObj is string))
                {
                    foreach (object o in men) AddOne(o);
                }
            }
            catch { }

            if (total == 0)
            {
                try
                {
                    var ien = entsObj as Il2CppSystem.Collections.IEnumerable;
                    if (ien != null)
                    {
                        var it = ien.GetEnumerator();
                        while (it != null && it.MoveNext()) AddOne(it.Current);
                    }
                }
                catch { }
            }

            if (total == 0) return null;
            foreach (var k in order)
                counts.Add(tally[k] > 1 ? k + "×" + tally[k] : k);
            return string.Join("、", counts.ToArray());
        }

        /// <summary>实体 -> 中文名：优先原型 GUID 查 name_map，再试 EntityName。</summary>
        private static string NameOfEntity(Entity e)
        {
            try
            {
                var a = e.GetArchetype();
                if (a != null && a.ArchetypeID != null)
                {
                    string n = Coach.NameOf(a.ArchetypeID.ToString());
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            catch { }
            try { return Coach.NameOf(e.EntityName) ?? e.EntityName; } catch { }
            return null;
        }

        private static System.Collections.IEnumerable EntitiesOf(object o)
        {
            if (o == null) return null;
            if (o is System.Collections.IEnumerable en && !(o is string)) return en;
            return null;
        }

        // ---------- 全局：当前冲突 + 版图 ----------

        /// <summary>
        /// 当前冲突（WormBoard.CurrentConflict）与版图占用（WormSpace.HasAgent/ControllingPlayer）。
        /// </summary>
        private static string RenderShared(Canis.Match match, ManualLogSource log)
        {
            var sb = new StringBuilder();
            try
            {
                var wm = match.TryCast<WormMatch>();
                if (wm != null)
                {
                    WormBoard board = null;
                    try { board = wm.WormPlaymat.Board; } catch { }
                    if (board != null)
                    {
                        AppendConflict(sb, board);
                        AppendSpaces(sb, board);
                    }
                }
            }
            catch (Exception e)
            {
                log.LogWarning("[STATE] shared: " + e.GetType().Name + ": " + e.Message);
            }
            if (sb.Length == 0) return null;
            return sb.ToString();
        }

        /// <summary>当前冲突：名字（name_map）+ 奖励文本（effect_map_v2 的 GUID 条目）。</summary>
        private static void AppendConflict(StringBuilder sb, WormBoard board)
        {
            try
            {
                var c = board.CurrentConflict;
                if (c == null) return;
                var ent = c.TryCast<Entity>();
                if (ent == null) return;

                string gid = null;
                try
                {
                    var a = ent.GetArchetype();
                    if (a != null && a.ArchetypeID != null) gid = a.ArchetypeID.ToString();
                }
                catch { }

                string nm = null;
                if (gid != null) nm = Coach.NameOf(gid);
                if (string.IsNullOrEmpty(nm))
                {
                    try { nm = Coach.NameOf(ent.EntityName) ?? ent.EntityName; } catch { }
                }
                if (string.IsNullOrEmpty(nm)) nm = "?";

                string eff = gid != null ? Coach.EffectOf(gid) : null;
                sb.Append("【当前冲突】").Append(nm);
                if (!string.IsNullOrEmpty(eff)) sb.Append("：").Append(eff);
                sb.Append('\n');
            }
            catch { }
        }

        /// <summary>版图占用：有特使的空间 + 有控制标记的空间，各一行压缩输出。</summary>
        private static void AppendSpaces(StringBuilder sb, WormBoard board)
        {
            try
            {
                var occupied = new List<string>();
                var controlled = new List<string>();
                var spaces = CopyIl2CppList<WormSpace>(board.Spaces);
                if (spaces == null) return;
                foreach (var s in spaces)
                {
                    if (s == null) continue;

                    string nm = SpaceName(s);
                    if (string.IsNullOrEmpty(nm)) continue;

                    try
                    {
                        if (s.HasAgent)
                        {
                            string owner = AgentOwners(s);
                            occupied.Add(nm + (string.IsNullOrEmpty(owner) ? "" : "(" + owner + ")"));
                        }
                    }
                    catch { }

                    try
                    {
                        var cp = s.ControllingPlayer;
                        if (cp != null)
                        {
                            string cn = null;
                            try { cn = cp.PlayerName; } catch { }
                            if (string.IsNullOrEmpty(cn))
                            {
                                try { cn = cp.ToString(); } catch { }
                            }
                            if (!string.IsNullOrEmpty(cn) && cn.Length < 30)
                                controlled.Add(nm + "→" + cn);
                        }
                    }
                    catch { }
                }
                if (occupied.Count > 0)
                    sb.Append("【特使已占】").Append(string.Join("、", occupied.ToArray())).Append('\n');
                if (controlled.Count > 0)
                    sb.Append("【控制标记】").Append(string.Join("、", controlled.ToArray())).Append('\n');
            }
            catch { }
        }

        /// <summary>把 interop 集合拷成托管 List（CardDiag 验证过的稳定遍历模式）；失败返回 null。</summary>
        private static List<T> CopyIl2CppList<T>(object src)
            where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            try
            {
                var l = new Il2CppSystem.Collections.Generic.List<T>(
                    (Il2CppSystem.Collections.Generic.IEnumerable<T>)src);
                var res = new List<T>();
                for (int i = 0; i < l.Count; i++) res.Add(l[i]);
                return res;
            }
            catch { return null; }
        }

        private static string SpaceName(WormSpace s)
        {
            try
            {
                var e = s.TryCast<Entity>();
                if (e == null) return null;
                try
                {
                    var a = e.GetArchetype();
                    if (a != null && a.ArchetypeID != null)
                    {
                        string n = Coach.NameOf(a.ArchetypeID.ToString());
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
                catch { }
                try { return Coach.NameOf(e.EntityName) ?? e.EntityName; } catch { }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 空间上的特使归属。Agents 元素的归属没有强类型接口，
        /// 只按属性名反射读 Owner/OwnerPlayer/ControllingPlayer/Player，
        /// 读到 PlayerEntity 才输出名字；取不到就返回 null（宁缺毋错）。
        /// </summary>
        private static string AgentOwners(WormSpace s)
        {
            try
            {
                List<WormAgent> agents = CopyIl2CppList<WormAgent>(s.Agents)
                                         ?? CopyIl2CppList<Entity>(s.Agents)?.Select(x => x.TryCast<WormAgent>()).ToList();
                if (agents == null) return null;
                var names = new List<string>();
                foreach (var a in agents)
                {
                    if (a == null) { names.Add("?"); continue; }
                    string who = OwnerNameOf(a);
                    names.Add(string.IsNullOrEmpty(who) ? "?" : who);
                }
                if (names.Count == 0) return null;
                names.Sort(StringComparer.Ordinal);
                return string.Join("、", names.ToArray());
            }
            catch { return null; }
        }

        /// <summary>按属性名反射找 PlayerEntity 归属。</summary>
        private static string OwnerNameOf(object o)
        {
            string[] props = { "Owner", "OwnerPlayer", "ControllingPlayer", "Player", "player" };
            foreach (var pn in props)
            {
                try
                {
                    var pi = o.GetType().GetProperty(pn,
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic);
                    if (pi == null) continue;
                    var v = pi.GetValue(o);
                    if (v == null) continue;
                    var pe = v as PlayerEntity;
                    if (pe == null && v is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                        pe = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)v).TryCast<PlayerEntity>();
                    if (pe != null)
                    {
                        string n = pe.PlayerName;
                        if (!string.IsNullOrEmpty(n)) return n;
                    }
                }
                catch { }
            }
            return null;
        }

        // ---------- 通用 ----------

        private static void TryAppend(StringBuilder sb, string label, Func<int> get)
        {
            try { sb.Append("｜").Append(label).Append(' ').Append(get()); }
            catch { }
        }

        private static bool _agentsOk;
        private static int _agentsFail;

        /// <summary>
        /// 可用特使数：RemainingAgents（强类型 IEnumerable&lt;WormAgent&gt;）。
        /// 开局领袖指派阶段底层集合未初始化会抛 NRE —— 失败可重试（前 3 次记日志），成功即输出。
        /// </summary>
        private static void AppendRemainingAgents(StringBuilder sb, WormPlayer wp, ManualLogSource log)
        {
            try
            {
                var rem = wp.RemainingAgents;
                if (rem == null)
                {
                    _agentsFail++;
                    if (_agentsFail <= 3) log.LogWarning("[STATE] diag: RemainingAgents=null (第" + _agentsFail + "次)");
                    return;
                }
                var l = new Il2CppSystem.Collections.Generic.List<WormAgent>(rem);
                if (l != null && l.Count > 0)
                {
                    if (!_agentsOk) { _agentsOk = true; log.LogWarning("[STATE] diag: RemainingAgents ok, count=" + l.Count); }
                    sb.Append("｜可用特使 ").Append(l.Count);
                }
                else
                {
                    _agentsFail++;
                    if (_agentsFail <= 3) log.LogWarning("[STATE] diag: RemainingAgents empty (第" + _agentsFail + "次)");
                }
            }
            catch (Exception e)
            {
                _agentsFail++;
                if (_agentsFail <= 3) log.LogWarning("[STATE] diag: agents " + e.GetType().Name + ": " + e.Message + " (第" + _agentsFail + "次)");
            }
        }

        /// <summary>按属性名反射取集合并输出元素个数；兼容 Il2Cpp 非托管枚举。</summary>
        private static void TryAppendReflectCount(StringBuilder sb, string label, object obj, string propName)
        {
            try
            {
                var pi = obj.GetType().GetProperty(propName,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic);
                if (pi == null) return;
                var v = pi.GetValue(obj);
                if (v == null) return;

                var cnt = v as System.Collections.ICollection;
                if (cnt != null) { sb.Append("｜").Append(label).Append(' ').Append(cnt.Count); return; }

                int c = Il2CppCount(v);
                if (c >= 0) sb.Append("｜").Append(label).Append(' ').Append(c);
            }
            catch { }
        }

        /// <summary>Il2Cpp 集合计数（interop 包装不实现托管 ICollection 时的兜底）；不可枚举返回 -1。</summary>
        private static int Il2CppCount(object v)
        {
            try
            {
                var ien = v as Il2CppSystem.Collections.IEnumerable;
                if (ien == null) return -1;
                var it = ien.GetEnumerator();
                int c = 0;
                while (it != null && it.MoveNext()) c++;
                return c;
            }
            catch { return -1; }
        }

        private static string SafeStr(PlayerEntity p)
        {
            try { return p.PlayerName ?? "?"; } catch { return "?"; }
        }
    }
}
