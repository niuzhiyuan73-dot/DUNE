using System;
using System.Collections.Generic;
using System.Text;
using Canis.entities;
using Canis.utils.ids;

namespace DuneAiProbe
{
    /// <summary>
    /// 一次性诊断：把棋盘上的每个「空间」实体连同它的子实体（效果能力）导出。
    ///
    /// 背景：存档里空间的效果是一串「能力 GUID」（属性 900013），但能力实体本身不落存档，
    /// 离线解不开；而空间在运行时是 Entity{EntityName="Space"}，其子实体就是那些能力。
    /// 所以这里在游戏线程里遍历 Match.Entities，按名字取出 Space，记录：
    ///   空间原型 GUID + 子实体名 + 子实体原型 GUID
    /// 之后与存档里的 (原型 GUID -> worm.space.X / 中文名) 关联，就能得到「空间 -> 能力」全表。
    ///
    /// 输出：dump/ai/spaces.txt（只写一次）
    /// </summary>
    internal static class SpaceDiag
    {
        private static bool _done;

        public static void Dump(Canis.Match match, BepInEx.Logging.ManualLogSource log)
        {
            if (_done || match == null) return;
            _done = true;
            try
            {
                var sb = new StringBuilder();
                sb.Append("# 空间实体诊断：原型 GUID | 子实体名 | 子实体原型 GUID\n");
                sb.Append("# entityName\tarchetypeGUID\tchildren\n");

                int total = 0, spaces = 0;
                var dict = match.Entities;
                if (dict == null) { log.LogWarning("[SPACEDIAG] match.Entities is null"); return; }
                total = dict.Count;

                foreach (var kv in dict)
                {
                    Entity e = null;
                    try { e = kv.Value; } catch { }
                    if (e == null) continue;

                    string name = null;
                    try { name = e.EntityName; } catch { }
                    if (name != "Space") continue;

                    spaces++;
                    string arch = Arch(e);

                    var kids = new List<string>();
                    try
                    {
                        var ch = e.Children;
                        if (ch != null)
                        {
                            for (int i = 0; i < ch.Count && i < 12; i++)
                            {
                                var c = ch[i];
                                if (c == null) continue;
                                string cn = null, ca = null;
                                try { cn = c.EntityName; } catch { }
                                ca = Arch(c);
                                kids.Add(cn + "[" + (ca == null ? "?" : ca) + "]");
                            }
                        }
                    }
                    catch { }

                    sb.Append("Space\t").Append(arch ?? "?").Append('\t')
                      .Append(string.Join(" | ", kids.ToArray())).Append('\n');
                }

                sb.Insert(0, "# 实体总数 " + total + "，其中 Space " + spaces + " 个\n");
                string dir = System.IO.Path.Combine(Plugin.RootDir, "dump", "ai");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "spaces.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                log.LogWarning("[SPACEDIAG] entities=" + total + " spaces=" + spaces + " -> " + path);
            }
            catch (Exception ex)
            {
                log.LogWarning("[SPACEDIAG] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string Arch(Entity e)
        {
            try
            {
                var a = e.GetArchetype();
                if (a != null && a.ArchetypeID != null) return a.ArchetypeID.ToString();
            }
            catch { }
            return null;
        }
    }
}
