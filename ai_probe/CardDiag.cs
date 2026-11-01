using System;
using System.Collections.Generic;
using System.Text;
using Canis.utils.ids;

namespace DuneAiProbe
{
    /// <summary>
    /// 一次性诊断：导出「AbilityID → 能力类名」全表。
    ///
    /// 为什么需要：存档里每张卡都带属性 900013 = 一串 AbilityID（GUID），
    /// 而效果文案在本地化 `worm.canis.abilities.&lt;类名&gt;` 下（380 条，338 条有中文）。
    /// 中间的 AbilityID → 类名 映射只在游戏运行时存在，离线拿不到。
    ///
    /// 做法：遍历 Archetypes 里的全部原型，挑出 AbilityArchetype，
    /// 读它的 ArchetypeAbilityID 与 EntityName（类名）。纯读取，不创建实体。
    ///
    /// 输出：dump/ai/ability_ids.tsv（只写一次）
    ///   AbilityID <TAB> 能力类名
    /// 之后由 mods/tools/join_cards.py 与存档里的卡牌属性合并。
    /// </summary>
    internal static class CardDiag
    {
        private static bool _done;

        public static void Dump(Canis.Match match, BepInEx.Logging.ManualLogSource log)
        {
            if (_done) return;
            _done = true;
            try
            {
                var inst = canis.archetypes.Archetypes._instance;
                if (inst == null) { log.LogWarning("[ABILITYMAP] Archetypes._instance is null"); return; }

                var sb = new StringBuilder();
                sb.Append("# AbilityID -> 能力类名（来自 AbilityArchetype）\r\n");

                int total = 0, ability = 0;
                var all = inst.AllArchetypes();
                if (all == null) { log.LogWarning("[ABILITYMAP] AllArchetypes is null"); return; }

                // Il2CppInterop 的 IEnumerator<T> 既不暴露 MoveNext 也不能转非泛型，
                // 改成先塞进 Il2Cpp 的 List<T>，再用下标遍历（SpaceDiag 里同样写法可用）。
                var list = new Il2CppSystem.Collections.Generic.List<canis.archetypes.Archetype>(all);
                int n = list.Count;
                for (int i = 0; i < n; i++)
                {
                    total++;
                    canis.archetypes.Archetype a = null;
                    try { a = list[i]; } catch { }
                    if (a == null) continue;

                    canis.archetypes.AbilityArchetype ab = null;
                    try { ab = a.TryCast<canis.archetypes.AbilityArchetype>(); } catch { }
                    if (ab == null) continue;

                    string id = null, name = null;
                    try { id = IdOf(ab.ArchetypeAbilityID); } catch { }
                    try { if (ab.EntityName != null) name = ab.EntityName.ToString(); } catch { }
                    if (string.IsNullOrEmpty(id)) continue;

                    ability++;
                    sb.Append(id).Append('\t').Append(name ?? "?").Append("\r\n");
                }

                sb.Insert(0, "# 原型总数 " + total + "，其中能力原型 " + ability + "\r\n");
                string dir = System.IO.Path.Combine(Plugin.RootDir, "dump", "ai");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "ability_ids.tsv");
                System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                log.LogWarning("[ABILITYMAP] archetypes=" + total + " abilities=" + ability + " -> " + path);
            }
            catch (Exception ex)
            {
                log.LogWarning("[ABILITYMAP] failed: " + ex.GetType().Name + ": " + ex.Message);
            }
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
