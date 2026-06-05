using System.Reflection;
using System.Text;

namespace RynthNav.Baker;

// Temporary: reflect the exact DotRecast 2026.1.3 public API for the types we
// need (build settings, builders, input geom, Detour query). Delete once the
// bake/query code is written against confirmed signatures.
internal static class ApiDump
{
    public static void Run()
    {
        string[] asmNames = { "DotRecast.Core", "DotRecast.Recast", "DotRecast.Recast.Toolset", "DotRecast.Detour" };
        var asms = new List<Assembly>();
        foreach (var n in asmNames)
        {
            try { asms.Add(Assembly.Load(new AssemblyName(n))); }
            catch (Exception ex) { Console.WriteLine($"load {n} FAILED: {ex.Message}"); }
        }
        Console.WriteLine("Loaded: " + string.Join(", ", asms.Select(a => a.GetName().Name + " " + a.GetName().Version)));

        string[] needles =
        {
            "NavMeshBuildSettings", "NavMeshBuilder", "InputGeomProvider", "IInputGeom", "RcInputGeom",
            "DtNavMeshQuery", "DtNavMeshCreateParams", "NavMeshBuildResult", "DtMeshData", "DtRawTileData",
            "DtMeshTile", "DtNavMeshParams", "DtMeshHeader", "RcByteOrder", "TileNavMeshBuilder", "RcNavMeshBuildSettings"
        };

        foreach (var a in asms)
        {
            Type[] types;
            try { types = a.GetExportedTypes(); }
            catch { continue; }
            foreach (var t in types.Where(t => t.FullName != null &&
                         (t.Name == "DtNavMesh" || needles.Any(nd => t.Name.Contains(nd, StringComparison.OrdinalIgnoreCase))))
                     .OrderBy(t => t.FullName))
                Dump(t);
        }
    }

    static void Dump(Type t)
    {
        var sb = new StringBuilder();
        string kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class";
        sb.AppendLine($"\n=== {t.FullName}  ({kind}) ===");
        if (t.IsEnum)
        {
            sb.AppendLine("  values: " + string.Join(", ", Enum.GetNames(t)));
            Console.WriteLine(sb.ToString());
            return;
        }
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            sb.AppendLine("  ctor(" + Params(c.GetParameters()) + ")");
        foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            sb.AppendLine($"  field {Short(f.FieldType)} {f.Name}");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            sb.AppendLine($"  prop  {Short(p.PropertyType)} {p.Name}");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Where(m => !m.IsSpecialName))
            sb.AppendLine($"  {Short(m.ReturnType)} {m.Name}({Params(m.GetParameters())})");
        Console.WriteLine(sb.ToString());
    }

    static string Params(ParameterInfo[] ps) =>
        string.Join(", ", ps.Select(p => (p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "") + Short(p.ParameterType) + " " + p.Name));

    static string Short(Type t)
    {
        if (t.IsByRef) t = t.GetElementType()!;
        if (t.IsGenericType)
        {
            var name = t.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name[..tick];
            return name + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">";
        }
        return t.Name;
    }
}
