using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// 导出程序集中指定类型的完整结构：基类、字段（含类型）、属性、方法签名。
// 用法: TypeDump <assembly.dll> <输出文件> <类型名子串1> [类型名子串2 ...]

internal sealed class SigProvider : ISignatureTypeProvider<string, object>
{
    private readonly MetadataReader _md;
    public SigProvider(MetadataReader md) { _md = md; }

    public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte raw)
    {
        var td = r.GetTypeDefinition(h);
        var ns = r.GetString(td.Namespace);
        var n = r.GetString(td.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte raw)
    {
        var tr = r.GetTypeReference(h);
        var ns = r.GetString(tr.Namespace);
        var n = r.GetString(tr.Name);
        return string.IsNullOrEmpty(ns) ? n : ns + "." + n;
    }
    public string GetTypeFromSpecification(MetadataReader r, object ctx, TypeSpecificationHandle h, byte raw)
        => r.GetTypeSpecification(h).DecodeSignature(this, ctx);

    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', s.Rank - 1) + "]";
    public string GetGenericInstantiation(string g, ImmutableArray<string> a)
        => g.Split('`')[0] + "<" + string.Join(", ", a) + ">";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetPointerType(string e) => e + "*";
    public string GetGenericTypeParameter(object ctx, int i) => "T" + i;
    public string GetGenericMethodParameter(object ctx, int i) => "M" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetModifiedType(string m, string u, bool req) => u;
    public string GetPinnedType(string e) => e;
}

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.WriteLine("用法: TypeDump <assembly.dll> <out.txt> <类型名子串...>");
            return 1;
        }

        var dll = args[0];
        var outPath = args[1];
        var filters = args.Skip(2).ToArray();

        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        if (!pe.HasMetadata) { Console.WriteLine("非托管程序集"); return 2; }
        var md = pe.GetMetadataReader();
        var prov = new SigProvider(md);
        var w = new StreamWriter(outPath);
        int hits = 0;

        foreach (var tdh in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(tdh);
            var name = md.GetString(td.Name);
            if (string.IsNullOrEmpty(name)) continue;
            var ns = md.GetString(td.Namespace);
            var full = string.IsNullOrEmpty(ns) ? name : ns + "." + name;

            if (!filters.Any(f => full.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)) continue;

            hits++;
            w.WriteLine("================================================================");
            w.WriteLine("TYPE " + full);

            try
            {
                if (!td.BaseType.IsNil)
                {
                    var bt = td.BaseType;
                    string bs = bt.Kind switch
                    {
                        HandleKind.TypeDefinition => prov.GetTypeFromDefinition(md, (TypeDefinitionHandle)bt, 0),
                        HandleKind.TypeReference => prov.GetTypeFromReference(md, (TypeReferenceHandle)bt, 0),
                        _ => bt.Kind.ToString()
                    };
                    w.WriteLine("  BASE " + bs);
                }
            }
            catch { }

            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                var fn = md.GetString(f.Name);
                // interop 生成的 Native*MethodInfoPtr/NativeFieldInfoPtr 噪音，跳过
                if (fn.StartsWith("NativeMethodInfoPtr_") || fn.StartsWith("NativeFieldInfoPtr_")) continue;
                string t;
                try { t = f.DecodeSignature(prov, null); }
                catch { t = "?"; }
                var attrs = f.Attributes.ToString();
                w.WriteLine($"  FIELD {t} {fn}   [{attrs}]");
            }

            foreach (var ph in td.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                string t;
                try { t = p.DecodeSignature(prov, null).ReturnType; }
                catch { t = "?"; }
                w.WriteLine($"  PROP {t} {md.GetString(p.Name)}");
            }

            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var mn = md.GetString(m.Name);
                if (string.IsNullOrEmpty(mn)) continue;
                string sig;
                try
                {
                    var s = m.DecodeSignature(prov, null);
                    var ps = new List<string>();
                    for (int i = 0; i < s.ParameterTypes.Length; i++)
                    {
                        var pn = "";
                        try
                        {
                            foreach (var ph2 in m.GetParameters())
                            {
                                var pp = md.GetParameter(ph2);
                                if (pp.SequenceNumber == i + 1) { pn = " " + md.GetString(pp.Name); break; }
                            }
                        }
                        catch { }
                        ps.Add(s.ParameterTypes[i] + pn);
                    }
                    sig = $"{s.ReturnType} {mn}({string.Join(", ", ps)})";
                }
                catch { sig = "? " + mn + "(...)"; }
                w.WriteLine("  METHOD " + sig);
            }
            w.WriteLine();
        }

        w.Close();
        Console.WriteLine($"匹配类型 {hits} 个 -> {outPath}");
        return 0;
    }
}
