using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx.Logging;
using Canis.entities;

namespace DuneAiProbe
{
    /// <summary>
    /// 版图结构枚举（每个对局一次）：把 Board 下的各个区域摊开写进 dump/ai/board.txt。
    ///
    /// 为什么需要它：
    ///   1) 存档里版图位置只有类名 "Space"，而 TargetMap 里给的是**实例 ID**；
    ///      只有把 Board.Spaces 逐个枚举出来，才知道「这个实例是哪个位置」。
    ///   2) 科技市场 / 研究轨 / 泰莱拉轨 这些区域在此之前完全没有读取入口，
    ///      模型只能写"可获取技术的具体选项未知"。它们的入口都在 WormBoard 上
    ///      （TechTileStacks / TechNegotiationArea / ResearchTrack / TleilaxuTrack）。
    ///
    /// 实现上全部走反射：不同版本这些属性的具体集合类型不一致，编译期绑定容易碎；
    /// 反射拿不到就跳过并记一行，不影响主流程。
    /// </summary>
    internal static class BoardMap
    {
        private static long _scanMatch;   // 已扫描过的对局指针（换局重扫）
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, string> _byId =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 科技市场里可获取的技术名（供局势块输出）
        private static readonly List<string> _tech = new List<string>();

        /// <summary>科技市场里可获取的技术（可能为空）。</summary>
        public static List<string> TechNames()
        {
            lock (_lock) return new List<string>(_tech);
        }

        /// <summary>按实体实例 ID 取位置名；没扫过或查不到返回 null。</summary>
        public static string NameOf(string entityId)
        {
            if (string.IsNullOrEmpty(entityId)) return null;
            lock (_lock) { string v; return _byId.TryGetValue(entityId, out v) ? v : null; }
        }

        public static void Scan(Canis.Match match, string root, ManualLogSource log)
        {
            if (match == null) return;
            long ptr = 0;
            try { ptr = match.Pointer.ToInt64(); } catch { }

            Entity boardEnt = null;
            try
            {
                var dict = match.Entities;
                if (dict == null) return;
                foreach (var kv in dict)
                {
                    Entity e = null;
                    try { e = kv.Value; } catch { }
                    if (e == null) continue;
                    string nm = null;
                    try { nm = e.EntityName; } catch { }
                    if (nm == "Board") { boardEnt = e; break; }
                }
            }
            catch (Exception e) { log.LogWarning("[BOARD] 找 Board: " + e.Message); return; }

            if (boardEnt == null) { log.LogWarning("[BOARD] 未找到 EntityName=Board 的实体"); return; }
            lock (_lock)
            {
                if (_scanMatch == ptr && ptr != 0) return;
                _scanMatch = ptr;
                _byId.Clear();
                _tech.Clear();
            }

            try
            {
                var wb = boardEnt.TryCast<worm.canis.entities.WormBoard>();
                if (wb == null) { log.LogWarning("[BOARD] Board 不是 WormBoard"); return; }

                var sb = new StringBuilder();
                sb.Append("# 版图结构（每局一次）\n");
                sb.Append("# 位置名来自 archetype GUID → name_map.tsv；空表示原型也解析不出\n\n");

                int nSpaces = DumpSpaces(sb, wb, log);
                DumpArea(sb, wb, "TechTileStacks", "科技市场", log);
                DumpArea(sb, wb, "TechNegotiationArea", "科技谈判区", log);
                DumpArea(sb, wb, "ResearchTrack", "研究轨", log);
                DumpArea(sb, wb, "TleilaxuTrack", "泰莱拉轨", log);
                DumpArea(sb, wb, "TleilaxuTank", "泰莱拉槽", log);
                DumpArea(sb, wb, "ShippingTrack", "运输轨", log);

                string path = Path.Combine(root, "dump", "ai", "board.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

                log.LogInfo("[BOARD] 扫描完成：位置 " + nSpaces + " 个，写入 dump/ai/board.txt");
            }
            catch (Exception e) { log.LogWarning("[BOARD] " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>版图位置：WormBoard.Spaces 是 Il2Cpp 的 IEnumerable&lt;WormSpace&gt;，要强类型包一层。</summary>
        private static int DumpSpaces(StringBuilder sb, worm.canis.entities.WormBoard wb, ManualLogSource log)
        {
            sb.Append("## 版图位置（Spaces）\n");
            sb.Append("# idx\ttype\tentityName\tarchetype\tname\tchildren\n");
            int n = 0, named = 0;
            try
            {
                var src = wb.Spaces;
                if (src == null) { sb.Append("（Spaces 为 null）\n"); return 0; }
                var list = new Il2CppSystem.Collections.Generic.List<worm.canis.entities.WormSpace>(src);
                for (int i = 0; i < list.Count; i++)
                {
                    var sp = list[i];
                    if (sp == null) continue;
                    var ent = sp.TryCast<Entity>();
                    string nm = null, arch = null, id = null, en = null;
                    int kids = 0;
                    if (ent != null)
                    {
                        try { en = ent.EntityName; } catch { }
                        try { kids = ent.Children != null ? ent.Children.Count : 0; } catch { }
                        try
                        {
                            var a = ent.GetArchetype();
                            if (a != null && a.ArchetypeID != null) arch = a.ArchetypeID.ToString();
                        }
                        catch { }
                        try { id = ent.EntityID != null ? ent.EntityID.ToString() : null; } catch { }
                    }
                    if (!string.IsNullOrEmpty(arch)) nm = Coach.NameOf(arch);
                    if (!string.IsNullOrEmpty(nm)) named++;
                    if (!string.IsNullOrEmpty(id))
                        lock (_lock) { _byId[id] = string.IsNullOrEmpty(nm) ? "位置" : nm; }

                    sb.Append(n).Append("\tSpace\t").Append(en ?? "")
                      .Append('\t').Append(arch ?? "").Append('\t').Append(nm ?? "")
                      .Append('\t').Append(kids).Append('\n');
                    n++;
                }
            }
            catch (Exception e)
            {
                sb.Append("（枚举失败: ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append("）\n");
            }
            sb.Append("# 共 ").Append(n).Append(" 个位置，其中 ").Append(named).Append(" 个解析出名字\n\n");
            if (log != null) log.LogInfo("[BOARD] 版图位置：" + n + " 个（解析出名字 " + named + "）");
            return n;
        }

        /// <summary>
        /// 其余区域（科技市场/研究轨/泰莱拉轨/运输轨）都是 Entity 子类，
        /// 没有集合接口，靠 Children 摊开；子项名同样按原型 GUID → name_map 解析。
        /// </summary>
        private static void DumpArea(StringBuilder sb, object board, string prop, string label, ManualLogSource log)
        {
            sb.Append("## ").Append(label).Append("（").Append(prop).Append("）\n");
            object val = null;
            try
            {
                var pi = board.GetType().GetProperty(prop);
                if (pi == null) { sb.Append("（属性不存在）\n\n"); return; }
                val = pi.GetValue(board);
            }
            catch (Exception e) { sb.Append("（读取失败: ").Append(e.GetType().Name).Append("）\n\n"); return; }
            if (val == null) { sb.Append("（null）\n\n"); return; }

            var bo = val as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            var ent = bo != null ? bo.TryCast<Entity>() : null;
            if (ent == null)
            {
                sb.Append("（不是 Entity: ").Append(val.GetType().Name).Append("）\n\n");
                return;
            }

            string ownName = null;
            try { ownName = Coach.NameOf(ent.EntityName); } catch { }
            sb.Append("# ").Append(prop).Append(" 自身=").Append(ent.EntityName ?? "")
              .Append(" 名字=").Append(ownName ?? "-").Append('\n');
            sb.Append("# idx\ttype\tentityName\tarchetype\tname\n");

            int n = 0, named = 0;
            try
            {
                var kids = ent.Children;
                if (kids != null)
                    for (int i = 0; i < kids.Count; i++)
                    {
                        var c = kids[i];
                        if (c == null) continue;
                        string cn = null, ca = null, cnm = null;
                        try { cn = c.EntityName; } catch { }
                        try
                        {
                            var a = c.GetArchetype();
                            if (a != null && a.ArchetypeID != null) ca = a.ArchetypeID.ToString();
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(ca)) cnm = Coach.NameOf(ca);
                        if (string.IsNullOrEmpty(cnm) && !string.IsNullOrEmpty(cn)) cnm = Coach.NameOf(cn);
                        if (!string.IsNullOrEmpty(cnm)) named++;
                        sb.Append(n).Append('\t').Append(c.GetType().Name)
                          .Append('\t').Append(cn ?? "").Append('\t').Append(ca ?? "")
                          .Append('\t').Append(cnm ?? "").Append('\n');
                        if (prop == "TechTileStacks" && !string.IsNullOrEmpty(cnm))
                            lock (_lock) { if (!_tech.Contains(cnm)) _tech.Add(cnm); }
                        n++;
                    }
            }
            catch (Exception e) { sb.Append("（children 失败: ").Append(e.Message).Append("）\n"); }

            sb.Append("# 共 ").Append(n).Append(" 项，其中 ").Append(named).Append(" 项解析出名字\n\n");
            if (log != null) log.LogInfo("[BOARD] " + label + "：" + n + " 项（解析出名字 " + named + "）");
        }

    }
}
