using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// 导出 .NET 程序集的类型与方法清单，用于离线分析游戏 API。
// 用法: ApiDump <assembly.dll> <output.txt> [关键字过滤]

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: ApiDump <assembly.dll> <output.txt> [filter]");
            return 1;
        }

        var dll = args[0];
        var outPath = args[1];
        var filter = args.Length > 2 ? args[2] : null;

        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata)
        {
            Console.WriteLine("不是托管程序集");
            return 2;
        }

        var md = pe.GetMetadataReader();
        var w = new StreamWriter(outPath);
        int types = 0, methods = 0;

        foreach (var tdh in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdh);
            var ns = md.GetString(td.Namespace);
            var name = md.GetString(td.Name);
            if (string.IsNullOrEmpty(name)) continue;
            var full = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";

            if (filter != null && full.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                // 类型名不匹配时仍可能方法名匹配，先看方法再决定
            }

            var lines = new System.Collections.Generic.List<string>();
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var mn = md.GetString(m.Name);
                if (string.IsNullOrEmpty(mn)) continue;
                lines.Add($"{full}::{mn}");
                methods++;
            }

            if (lines.Count == 0) continue;
            if (filter != null)
                lines = lines.Where(l => l.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (lines.Count == 0) continue;

            types++;
            foreach (var l in lines) w.WriteLine(l);
        }

        w.Close();
        Console.WriteLine($"类型 {types}, 方法行 {methods}, 输出 {outPath}");
        return 0;
    }
}
