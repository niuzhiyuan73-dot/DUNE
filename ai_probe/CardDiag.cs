using System;
using System.Collections.Generic;
using System.Text;
using Canis.entities;

namespace DuneAiProbe
{
    /// <summary>
    /// 一次性诊断：把「卡牌/领袖/空间 → 其效果能力实体」全部导出。
    ///
    /// 为什么需要：本地化里 106 张卡只有标题没有效果文本（基础包与部分扩展是图标化表达），
    /// 但这些卡的效果在游戏里是**能力实体**实现的，而能力名对应的
    /// `worm.canis.abilities.&lt;类名&gt;` 在本地化里有官方中文（380 条中 338 条有文案）。
    /// 所以只要能拿到「卡 → 能力名」，就能反推效果，不需要外部卡表。
    ///
    /// 做法：遍历 Match.Entities，对每个能映射到名称表（原型 GUID 或实体名）的实体，
    /// 记录其 Descendents 里属于能力类的子实体名。
    ///
    /// 输出：dump/ai/abilities.txt（只写一次）
    ///   entityName	原型GUID	中文名	能力名1|能力名2|...
    /// </summary>
    internal static class CardDiag
    {
        private static bool _done;

        public static void Dump(Canis.Match match, BepInEx.Logging.ManualLogSource log)
        {
            if (_done || match == null) return;
            _done = true;
            try
            {
                var sb = new StringBuilder();
                sb.Append("# 实体 -> 效果能力 导出\r\n");
                sb.Append("# entityName\tarchetypeGUID\t中文名\t能力名列表\r\n");

                int total = 0, kept = 0, withAbil = 0;
                var seenArch = new HashSet<string>(StringComparer.Ordinal);

                var dict = match.Entities;
                if (dict == null) { log.LogWarning("[CARDDIAG] match.Entities is null"); return; }
                total = dict.Count;

                foreach (var kv in dict)
                {
                    Entity e = null;
                    try { e = kv.Value; } catch { }
                    if (e == null) continue;

                    string en = null, arch = null;
                    try { en = e.EntityName; } catch { }
                    try
                    {
                        var a = e.GetArchetype();
                        if (a != null && a.ArchetypeID != null) arch = a.ArchetypeID.ToString();
                    }
                    catch { }

                    // 用原型 GUID 查名称表；查不到就跳过（多半是内部状态实体，没有效果）
                    string human = arch != null ? Coach.NameOf(arch) : null;
                    if (human == null) continue;

                    string key = arch ?? en ?? "";
                    if (!seenArch.Add(key)) continue;   // 同原型只记一次

                    kept++;
                    var abil = new List<string>();
                    try
                    {
                        var desc = e.Descendents;
                        if (desc != null)
                        {
                            for (int i = 0; i < desc.Count && abil.Count < 16; i++)
                            {
                                Entity d = null;
                                try { d = desc[i]; } catch { }
                                if (d == null) continue;
                                string dn = null;
                                try { dn = d.EntityName; } catch { }
                                if (string.IsNullOrEmpty(dn)) continue;

                                // 只收确实是能力类的（本地化有键或有文案）
                                string text = Coach.NameOf("worm.canis.abilities." + dn);
                                if (text == null) continue;
                                if (!abil.Contains(dn)) abil.Add(dn);
                            }
                        }
                    }
                    catch { }

                    if (abil.Count > 0) withAbil++;
                    sb.Append(en ?? "?").Append('\t')
                      .Append(arch ?? "?").Append('\t')
                      .Append(human).Append('\t')
                      .Append(string.Join("|", abil.ToArray())).Append("\r\n");
                }

                sb.Insert(0, "# 实体总数 " + total + "，命中名称表 " + kept + "，其中带能力 " + withAbil + "\r\n");
                string dir = System.IO.Path.Combine(Plugin.RootDir, "dump", "ai");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "abilities.txt");
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                log.LogWarning("[CARDDIAG] entities=" + total + " kept=" + kept
                               + " withAbilities=" + withAbil + " -> " + path);
            }
            catch (Exception ex)
            {
                log.LogWarning("[CARDDIAG] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
