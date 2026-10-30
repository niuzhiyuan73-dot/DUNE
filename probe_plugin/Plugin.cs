using System;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using canis.archetypes;
using Canis.attributes;
using worm.canis.actions.phases;
using worm.canis.data.enums;
using ArchList = Il2CppSystem.Collections.Generic.List<canis.archetypes.Archetype>;
using ArchIEnum = Il2CppSystem.Collections.Generic.IEnumerable<canis.archetypes.Archetype>;
using CostList = Il2CppSystem.Nullable<int>;

namespace DuneProbe
{
    /// 导出全部卡牌的花费 / tag / 阵营 / 内容集到 JSON（游戏内执行）。
    [BepInPlugin(GUID, "Dune CardExport", "1.0.0")]
    public class Plugin : BasePlugin
    {
        public const string GUID = "local.dunecardexport";
        private const string OUT = @"C:/Users/17789/Desktop/DUNE/dump/cards_runtime.json";

        internal static ManualLogSource L;
        private static bool _done;

        public override void Load()
        {
            L = Log;
            L.LogInfo("========== [EXPORT] card dump ==========");
            var h = new Harmony("local.dunecardexport.harmony");
            var sp = typeof(SetupPhase).GetMethod("SetupPlayer", BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (sp != null)
                h.Patch(sp, prefix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(OnSetup),
                    BindingFlags.Static | BindingFlags.NonPublic)));
            else L.LogWarning("[EXPORT] SetupPlayer not found");
        }

        private static void OnSetup(SetupPhase __instance)
        {
            if (_done) return;
            _done = true;
            try { Dump(); } catch (Exception e) { L.LogWarning("[EXPORT] " + e.GetType().Name + ": " + e.Message); }
        }

        private static void Dump()
        {
            var reg = Archetypes.GetInstance("worm");
            if (reg == null) { L.LogWarning("[EXPORT] no registry"); return; }

            var all = reg.AllArchetypes();
            if (all == null) { L.LogWarning("[EXPORT] AllArchetypes null"); return; }

            var list = ToList(all);
            if (list == null) { L.LogWarning("[EXPORT] enum failed"); return; }
            L.LogInfo("[EXPORT] archetypes = " + list.Count);

            var defImage = AttributeDefinitions.GetDefinition<string>("Image") ?? AttributeDefinitions.GetDefinition<string>(920003);
            var defCost = AttributeDefinitions.GetDefinition<CostList>("PersuasionCost");
            if (defCost == null) defCost = AttributeDefinitions.GetDefinition<CostList>(920006);
            var defTags = AttributeDefinitions.GetDefinition<Il2CppSystem.Collections.Generic.List<CardTags>>("Tags");
            var defFaction = AttributeDefinitions.GetDefinition<Il2CppSystem.Collections.Generic.List<Factions>>("FactionList");
            var defSet = AttributeDefinitions.GetDefinition<Il2CppSystem.Collections.Generic.List<Set>>("SetList");
            var defImp = AttributeDefinitions.GetDefinition<Il2CppSystem.Nullable<ImperiumTypes>>("ImperiumType");
            var defType = AttributeDefinitions.GetDefinition<Il2CppSystem.Nullable<EntityTypes>>("EntityType");

            L.LogInfo("[EXPORT] image key=" + KeyOf(defImage) + " cost key=" + KeyOf(defCost)
                      + " tags key=" + KeyOf(defTags) + " faction key=" + KeyOf(defFaction)
                      + " set key=" + KeyOf(defSet) + " imperium key=" + KeyOf(defImp)
                      + " type key=" + KeyOf(defType));

            var sb = new StringBuilder();
            sb.Append("[\n");
            int n = 0;

            for (int i = 0; i < list.Count; i++)
            {
                var a = list[i];
                if (a == null) continue;

                string image = null;
                try { image = a.GetAttribute(defImage)?.Value; } catch { }
                if (string.IsNullOrEmpty(image)) continue;

                string type = "";
                try { var t = a.GetAttribute(defType)?.Value; if (t != null && t.HasValue) type = t.Value.ToString(); } catch { }
                string imp = "";
                try { var t2 = a.GetAttribute(defImp)?.Value; if (t2 != null && t2.HasValue) imp = t2.Value.ToString(); } catch { }

                int? cost = null;
                string dbg = "";
                if (defCost != null)
                {
                    try
                    {
                        var attr = a.GetAttribute(defCost);
                        if (attr != null)
                        {
                            var ro = attr.AsReadOnly;
                            var v1 = ro?.Value;
                            var v2 = ro?.BaseValue;
                            var v3 = ro?.ModValue;
                            dbg = " ro.Value=" + Fmt(v1) + " base=" + Fmt(v2) + " mod=" + Fmt(v3);
                            cost = ToInt(v1) ?? ToInt(v2) ?? ToInt(v3);
                        }
                        if (cost == null)
                        {
                            var c = attr?.Value;
                            if (c != null && c.HasValue) cost = c.Value;
                            dbg += " nullable=" + (c == null ? "null" : (c.HasValue ? c.Value.ToString() : "noValue"));
                        }
                    }
                    catch (Exception ex) { dbg += " ERR:" + ex.Message; }
                }
                if (n < 12) L.LogInfo("[EXPORT] sample " + image + " cost=" + (cost.HasValue ? cost.Value.ToString() : "null") + dbg);

                var tags = Names(a, defTags);
                var factions = Names(a, defFaction);
                var sets = Names(a, defSet);

                if (n++ > 0) sb.Append(",\n");
                sb.Append("{\"id\":\"").Append(Esc(a.ArchetypeID?.ToString())).Append("\"");
                sb.Append(",\"image\":\"").Append(Esc(image)).Append("\"");
                sb.Append(",\"type\":\"").Append(type).Append("\"");
                sb.Append(",\"imp\":\"").Append(imp).Append("\"");
                sb.Append(",\"cost\":").Append(cost.HasValue ? cost.Value.ToString() : "null");
                sb.Append(",\"tags\":[").Append(tags).Append("]");
                sb.Append(",\"factions\":[").Append(factions).Append("]");
                sb.Append(",\"sets\":[").Append(sets).Append("]}");
            }

            sb.Append("\n]\n");
            System.IO.File.WriteAllText(OUT, sb.ToString(), Encoding.UTF8);
            L.LogWarning("[EXPORT] >>> wrote " + n + " cards -> " + OUT);
        }

        private static ArchList ToList(ArchIEnum src)
        {
            try
            {
                ConstructorInfo ctor = null;
                foreach (var c in typeof(ArchList).GetConstructors())
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 1 && (ps[0].ParameterType.FullName ?? "").Contains("IEnumerable")) ctor = c;
                }
                if (ctor == null) return null;
                var castM = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .First(x => x.Name == "TryCast" && x.IsGenericMethod);
                var casted = castM.MakeGenericMethod(typeof(ArchIEnum)).Invoke(src, null);
                if (casted == null) return null;
                return ctor.Invoke(new[] { casted }) as ArchList;
            }
            catch (Exception e) { L.LogWarning("[EXPORT] ToList: " + e.Message); return null; }
        }

        private static string Names<T>(Archetype a, AttributeDefinition<Il2CppSystem.Collections.Generic.List<T>> def)
        {
            var sb = new StringBuilder();
            try
            {
                var l = a.GetAttribute(def)?.Value;
                if (l == null) return sb.ToString();
                for (int i = 0; i < l.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("\"").Append(Esc(l[i]?.ToString())).Append("\"");
                }
            }
            catch { }
            return sb.ToString();
        }

        private static int? ToInt(object o)
        {
            if (o == null) return null;
            try
            {
                var s = o.ToString();
                if (string.IsNullOrEmpty(s)) return null;
                int v;
                if (int.TryParse(s, out v)) return v;
            }
            catch { }
            return null;
        }

        private static string Fmt(object o)
        {
            try { return o == null ? "null" : o.ToString(); } catch { return "?"; }
        }

        private static string KeyOf<T>(Canis.attributes.AttributeDefinition<T> def)
        {
            try { return def == null ? "null" : def.Key.ToString(); } catch { return "?"; }
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
