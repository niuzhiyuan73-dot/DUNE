using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Canis.utils.ids;

namespace DuneAiProbe
{
    /// <summary>
    /// 一次性诊断：导出「AbilityID → 能力类名」映射。
    ///
    /// 为什么需要：存档里每张卡带属性 900013 = 一串 AbilityID（GUID），
    /// 而效果文案在本地化 `worm.canis.abilities.&lt;类名&gt;` 下（380 条，338 条有中文）。
    /// 中间的 AbilityID → 类名 映射只在运行时存在，离线拿不到。
    ///
    /// 两条路都试：
    ///   1) 遍历 Archetypes 里的 AbilityArchetype（快，但实测只返回 1 个——能力原型不在该表里）；
    ///   2) 读 mods/ai/ability_ids_input.txt（由 join_cards 从存档导出的 900013 清单），
    ///      逐个让 IAbilityEntityFactory 造实体，读 EntityName.Name。
    ///
    /// 输出：dump/ai/ability_ids.tsv（只写一次）
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

                var sb = new StringBuilder();
                sb.Append("# AbilityID\t能力类名\r\n");

                int viaArch = 0, viaFactory = 0, miss = 0, fail = 0;

                // 路线 1：已加载的原型表（能力原型通常不在这里，留着兜底）
                try
                {
                    var all = inst.AllArchetypes();
                    if (all != null)
                    {
                        var list = new Il2CppSystem.Collections.Generic.List<canis.archetypes.Archetype>(all);
                        for (int i = 0; i < list.Count; i++)
                        {
                            canis.archetypes.Archetype a = null;
                            try { a = list[i]; } catch { }
                            if (a == null) continue;

                            canis.archetypes.AbilityArchetype ab = null;
                            try { ab = a.TryCast<canis.archetypes.AbilityArchetype>(); } catch { }
                            if (ab == null) continue;

                            string id = null, name = null;
                            try { id = IdOf(ab.ArchetypeAbilityID); } catch { }
                            try { name = NameOf(ab.EntityName); } catch { }
                            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                            sb.Append(id).Append('\t').Append(name).Append("\r\n");
                            viaArch++;
                        }
                    }
                }
                catch (Exception e) { log.LogWarning("[ABILITYMAP] arch scan: " + e.Message); }

                // 路线 2：按存档里的 ID 清单，让工厂造实体读名字
                try
                {
                    var factory = inst.AbilityFactory;
                    string input = Path.Combine(Plugin.RootDir, "mods", "ai", "ability_ids_input.txt");
                    if (factory == null)
                    {
                        log.LogWarning("[ABILITYMAP] AbilityFactory is null");
                    }
                    else if (!File.Exists(input))
                    {
                        log.LogWarning("[ABILITYMAP] 缺少 " + input + "（先跑 mods/tools/join_cards.py 导出）");
                    }
                    else
                    {
                        var lines = File.ReadAllLines(input);
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            string s = lines[i].Trim();
                            if (s.Length == 0 || s.StartsWith("#")) continue;
                            if (!seen.Add(s)) continue;

                            try
                            {
                                AbilityID aid = new AbilityID(s);
                                if (!factory.HasEntry(aid)) { miss++; continue; }
                                var ent = factory.CreateEntity(aid, match, null, null);
                                if (ent == null) { fail++; continue; }
                                string nm = null;
                                try { nm = ent.EntityName; } catch { }
                                if (string.IsNullOrEmpty(nm)) { fail++; continue; }
                                sb.Append(s).Append('\t').Append(nm).Append("\r\n");
                                viaFactory++;
                            }
                            catch { fail++; }
                        }
                    }
                }
                catch (Exception e) { log.LogWarning("[ABILITYMAP] factory: " + e.Message); }

                sb.Insert(0, "# 原型表 " + viaArch + " 条；工厂 " + viaFactory + " 条；"
                              + "ID 无条目 " + miss + "；取名字失败 " + fail + "\r\n");
                string dir = Path.Combine(Plugin.RootDir, "dump", "ai");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "ability_ids.tsv");
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                log.LogWarning("[ABILITYMAP] arch=" + viaArch + " factory=" + viaFactory
                               + " miss=" + miss + " fail=" + fail + " -> " + path);
            }
            catch (Exception ex)
            {
                log.LogWarning("[ABILITYMAP] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// IEntityName 是接口，直接 ToString 只会得到类型名，必须取 Name
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

        /// TypedID 的字符串值：优先反射 _ID / ID，退回 ToString
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
