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

                int nSpaces = DumpList(sb, wb, "Spaces", "版图位置", log, true);
                DumpList(sb, wb, "TechTileStacks", "科技市场", log, false);
                DumpList(sb, wb, "TechNegotiationArea", "科技谈判区", log, false);
                DumpList(sb, wb, "ResearchTrack", "研究轨", log, false);
                DumpList(sb, wb, "TleilaxuTrack", "泰莱拉轨", log, false);
                DumpList(sb, wb, "TleilaxuTank", "泰莱拉槽", log, false);
                DumpList(sb, wb, "ShippingTrack", "运输轨", log, false);

                string path = Path.Combine(root, "dump", "ai", "board.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

                log.LogInfo("[BOARD] 扫描完成：位置 " + nSpaces + " 个，写入 dump/ai/board.txt");
            }
            catch (Exception e) { log.LogWarning("[BOARD] " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>把一个区域属性当集合摊开：序号 | 类型 | entityName | 原型GUID | 名字 | 占用/子项数。</summary>
        private static int DumpList(StringBuilder sb, object board, string prop, string label,
                                    ManualLogSource log, bool recordSpaces)
        {
            object val = null;
            try
            {
                var pi = board.GetType().GetProperty(prop);
                if (pi == null) { sb.Append("## ").Append(label).Append("（属性 ").Append(prop).Append(" 不存在）\n"); return 0; }
                val = pi.GetValue(board);
            }
            catch (Exception e)
            {
                sb.Append("## ").Append(label).Append("（读取失败: ").Append(e.GetType().Name).Append("）\n");
                return 0;
            }
            if (val == null) { sb.Append("## ").Append(label).Append("（null）\n"); return 0; }

            var en = val as System.Collections.IEnumerable;
            if (en == null)
            {
                sb.Append("## ").Append(label).Append("（非集合: ").Append(val.GetType().Name).Append("）\n");
                return 0;
            }

            sb.Append("## ").Append(label).Append("（").Append(prop).Append("）\n");
            sb.Append("# idx\ttype\tentityName\tarchetype\tname\tchildren\n");

            int n = 0, named = 0;
            var lastNames = new List<string>();
            foreach (object o in en)
            {
                if (o == null) continue;
                string typeName = null, entityName = null, arch = null, nm = null;
                int kids = 0;
                string id = null;
                try { typeName = o.GetType().Name; } catch { }
                try
                {
                    var bo = o as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                    var ent = bo != null ? bo.TryCast<Entity>() : null;
                    if (ent != null)
                    {
                        try { entityName = ent.EntityName; } catch { }
                        try { kids = ent.Children != null ? ent.Children.Count : 0; } catch { }
                        try
                        {
                            var a = ent.GetArchetype();
                            if (a != null && a.ArchetypeID != null) arch = a.ArchetypeID.ToString();
                        }
                        catch { }
                        try { id = ent.EntityID != null ? ent.EntityID.ToString() : null; } catch { }
                    }
                }
                catch { }

                if (!string.IsNullOrEmpty(arch)) nm = Coach.NameOf(arch);
                if (string.IsNullOrEmpty(nm) && !string.IsNullOrEmpty(entityName) && entityName != "Space")
                    nm = Coach.NameOf(entityName);
                if (!string.IsNullOrEmpty(nm)) named++;
                lastNames.Add(nm);

                if (recordSpaces && !string.IsNullOrEmpty(id))
                {
                    lock (_lock) { _byId[id] = string.IsNullOrEmpty(nm) ? "位置" : nm; }
                }

                sb.Append(n).Append('\t').Append(typeName)
                  .Append('\t').Append(entityName ?? "")
                  .Append('\t').Append(arch ?? "")
                  .Append('\t').Append(nm ?? "")
                  .Append('\t').Append(kids).Append('\n');
                n++;
            }

            sb.Append("# 共 ").Append(n).Append(" 项，其中 ").Append(named).Append(" 项解析出名字\n\n");
            if (prop == "TechTileStacks")
            {
                lock (_lock)
                {
                    for (int i = 0; i < lastNames.Count; i++)
                        if (!string.IsNullOrEmpty(lastNames[i]) && !_tech.Contains(lastNames[i]))
                            _tech.Add(lastNames[i]);
                }
            }
            if (log != null) log.LogInfo("[BOARD] " + label + "：" + n + " 项（解析出名字 " + named + "）");
            return n;
        }
    }
}
