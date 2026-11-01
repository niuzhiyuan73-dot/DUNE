using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Canis.utils.ids;

namespace DuneAiProbe
{
    /// <summary>
    /// 一次性诊断：导出「原型 → 能力 ID 列表」与「能力 ID → 类名」。
    ///
    /// 背景：本地化对 106 张卡只给了标题，效果是图标化表达。存档里的 Abilities 子树
    /// 只含**已实例化**的能力，缺条件能力与未触发的分支。卡牌 archetype 上的
    /// AbilityIDsAttribute（List&lt;AbilityID&gt;）才是完整清单。
    ///
    /// 上一版失败原因：先调 IAbilityEntityFactory.HasEntry(id) 判存在，281 个全 false。
    /// 工厂是懒加载的，HasEntry 只看缓存。这一版跳过 HasEntry，直接 CreateEntity。
    ///
    /// 输出（只写一次）：
    ///   dump/ai/arch_abilities.tsv  原型类名/ID <TAB> 能力ID 列表
    ///   dump/ai/ability_ids.tsv     AbilityID <TAB> 能力类名
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
                var inst = canis.archetypes.Archetypes._instance;
                if (inst == null) { log.LogWarning("[ABILITYMAP] Archetypes._instance is null"); return; }

                string dir = Path.Combine(Plugin.RootDir, "dump", "ai");
                Directory.CreateDirectory(dir);

                var archSb = new StringBuilder();
                archSb.Append("# 原型类名\t原型ID\t能力ID列表\r\n");

                var idSb = new StringBuilder();
                idSb.Append("# AbilityID\t能力类名\r\n");

                var all = inst.AllArchetypes();
                if (all == null) { log.LogWarning("[ABILITYMAP] AllArchetypes is null"); return; }
                var list = new Il2CppSystem.Collections.Generic.List<canis.archetypes.Archetype>(all);

                int archCount = 0, withAbil = 0, idCount = 0, resolved = 0, fail = 0;
                var seenId = new HashSet<string>(StringComparer.Ordinal);
                var factory = inst.AbilityFactory;

                for (int i = 0; i < list.Count; i++)
                {
                    canis.archetypes.Archetype a = null;
                    try { a = list[i]; } catch { }
                    if (a == null) continue;
                    archCount++;

                    string name = null, archId = null;
                    try { name = NameOf(a.EntityName); } catch { }
                    try { if (a.ArchetypeID != null) archId = IdOf(a.ArchetypeID); } catch { }

                    // 读 AbilityIDsAttribute（AttributeDefinition<List<AbilityID>>）
                    var ids = new List<string>();
                    try
                    {
                        var lst = a.GetAttributeValue<Il2CppSystem.Collections.Generic.List<Canis.utils.ids.AbilityID>>(
                            a.AbilityIDsAttribute, null);
                        if (lst != null)
                        {
                            for (int k = 0; k < lst.Count; k++)
                            {
                                string s = null;
                                try { s = IdOf(lst[k]); } catch { }
                                if (!string.IsNullOrEmpty(s)) ids.Add(s);
                            }
                        }
                    }
                    catch (Exception e) { if (i < 3) log.LogWarning("[ABILITYMAP] attr: " + e.Message); }

                    if (ids.Count > 0) withAbil++;
                    archSb.Append(name ?? "?").Append('\t').Append(archId ?? "?")
                          .Append('\t').Append(string.Join(",", ids.ToArray())).Append("\r\n");

                    // 每个新 ID 造一次实体拿类名（不做 HasEntry，工厂懒加载）
                    if (factory == null) continue;
                    for (int k = 0; k < ids.Count; k++)
                    {
                        string aid = ids[k];
                        if (!seenId.Add(aid)) continue;
                        idCount++;
                        try
                        {
                            var ent = factory.CreateEntity(new AbilityID(aid), match, null, null);
                            string nm = null;
                            try { if (ent != null) nm = ent.EntityName; } catch { }
                            if (string.IsNullOrEmpty(nm)) { fail++; continue; }
                            idSb.Append(aid).Append('\t').Append(nm).Append("\r\n");
                            resolved++;
                        }
                        catch { fail++; }
                    }
                }

                File.WriteAllText(Path.Combine(dir, "arch_abilities.tsv"),
                    "# 原型 " + archCount + "，带能力 " + withAbil + "\r\n" + archSb,
                    new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "ability_ids.tsv"),
                    "# 唯一 ID " + idCount + "，解析成功 " + resolved + "，失败 " + fail + "\r\n" + idSb,
                    new UTF8Encoding(false));

                log.LogWarning("[ABILITYMAP] arch=" + archCount + " withAbilities=" + withAbil
                               + " ids=" + idCount + " resolved=" + resolved + " failed=" + fail
                               + " -> " + dir);
            }
            catch (Exception ex)
            {
                log.LogWarning("[ABILITYMAP] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string NameOf(Canis.entities.IEntityName en)
        {
            if (en == null) return null;
            try
            {
                string n = en.Name;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return null;
        }

        private static string IdOf(object typed)
        {
            if (typed == null) return null;
            Type t = typed.GetType();
            string[] names = { "_ID", "ID", "Value" };
            for (int i = 0; i < names.Length; i++)
            {
                try
                {
                    var pi = t.GetProperty(names[i]);
                    if (pi == null) continue;
                    object v = pi.GetValue(typed);
                    if (v != null)
                    {
                        string s = v.ToString();
                        if (!string.IsNullOrEmpty(s) && s.IndexOf('.') < 0) return s;
                    }
                }
                catch { }
            }
            try
            {
                string r = typed.ToString();
                if (!string.IsNullOrEmpty(r) && r.IndexOf('.') < 0) return r;
            }
            catch { }
            return null;
        }
    }
}
