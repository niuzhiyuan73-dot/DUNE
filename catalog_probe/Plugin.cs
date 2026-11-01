using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using canis.archetypes;
using Canis.attributes;

namespace DuneCatalogProbe
{
    // Reads already registered archetypes only. Never builds entities or invokes abilities.
    [BepInPlugin("local.dunecatalog.readonly", "Dune Read-only Catalog", "1.0.0")]
    public sealed class Plugin : BasePlugin
    {
        private static BepInEx.Logging.ManualLogSource logger;
        private static bool done;
        private static int attempts;
        public override void Load()
        {
            logger = Log;
            var method = typeof(worm.canis.actions.phases.SetupPhase).GetMethod("SetupPlayer",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method == null) throw new MissingMethodException("SetupPhase.SetupPlayer");
            new Harmony("local.dunecatalog.readonly").Patch(method,
                postfix: new HarmonyMethod(typeof(Plugin).GetMethod(nameof(Export), BindingFlags.NonPublic | BindingFlags.Static)));
            logger.LogInfo("[CATALOG] read-only exporter ready");
            AddComponent<CatalogPoll>();
        }
        public sealed class CatalogPoll : UnityEngine.MonoBehaviour
        {
            private float next;
            public CatalogPoll(IntPtr pointer) : base(pointer) { }
            public void Update()
            {
                if (done || UnityEngine.Time.realtimeSinceStartup < next) return;
                next = UnityEngine.Time.realtimeSinceStartup + 5;
                // Only inspect an existing registry; never initialize a match.
                if (Archetypes._instance != null) Export();
            }
        }
        private static string Quote(string s) => System.Text.Json.JsonSerializer.Serialize(s);
        private static void Export()
        {
            if (done) return;
            try
            {
                var registry = Archetypes.GetInstance("worm");
                if (registry == null) return;
                var all = new Il2CppSystem.Collections.Generic.List<Archetype>(registry.AllArchetypes());
                if (all.Count == 0) return;
                var root = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, ".."));
                var dir = Path.Combine(root, "dump", "current_mode");
                Directory.CreateDirectory(dir);
                var rows = new StringBuilder("[\n");
                int count = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    var a = all[i];
                    if (a == null) continue;
                    // SerializeAttributes is the engine's read path; do not call Build/CreateEntity.
                    string attrs = Canis.json.JSON.ToJSON(a.SerializeAttributes(), new Canis.json.JSON.Options(false, ""));
                    if (count++ > 0) rows.Append(",\n");
                    rows.Append("{\"id\":").Append(Quote(a.ArchetypeID.ToString()))
                        .Append(",\"class\":").Append(Quote(a.GetIl2CppType().FullName))
                        .Append(",\"attributes\":").Append(attrs).Append('}');
                }
                rows.Append("\n]");
                File.WriteAllText(Path.Combine(dir, "archetypes.json"), rows.ToString(), new UTF8Encoding(false));
                var defs = new StringBuilder("[\n");
                int n = 0;
                foreach (var kv in AttributeDefinitions._definitionsByKey)
                {
                    var def = kv.Value;
                    if (n++ > 0) defs.Append(",\n");
                    defs.Append("{\"key\":").Append(def.Key).Append(",\"name\":")
                        .Append(Quote(def.Name)).Append(",\"type\":").Append(Quote(def.ValueType.FullName)).Append('}');
                }
                defs.Append("\n]");
                File.WriteAllText(Path.Combine(dir, "attribute_definitions.json"), defs.ToString(), new UTF8Encoding(false));
                done = true;
                logger.LogInfo("[CATALOG] exported " + count + " archetypes, " + n + " attribute definitions");
            }
            catch (Exception e) { done = ++attempts >= 3; logger.LogError("[CATALOG] " + e); }
        }
    }
}
