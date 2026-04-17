using System.Reflection;
using System.Runtime.Loader;

// Local utility to introspect Neverway.dll via reflection.
// Usage:
//   dotnet run --project Tools/NeverwayInspector -- /gay/editme/nw-again/Neverway.dll

string neverwayPath = args.FirstOrDefault() ?? "/gay/editme/nw-again/Neverway.dll";
neverwayPath = Path.GetFullPath(neverwayPath);
string baseDir = Path.GetDirectoryName(neverwayPath)!;

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string candidate = Path.Combine(baseDir, $"{name.Name}.dll");
    if (File.Exists(candidate))
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);

    return null;
};

Console.WriteLine($"Loading: {neverwayPath}");
var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(neverwayPath);

static bool IsPetRelevant(string fullName) =>
    fullName.Contains("Pet", StringComparison.OrdinalIgnoreCase)
    || fullName.Contains("Companion", StringComparison.OrdinalIgnoreCase)
    || fullName.Contains("Familiar", StringComparison.OrdinalIgnoreCase);

var types = asm.GetTypes()
    .Where(t => t.FullName is string fn && IsPetRelevant(fn))
    .OrderBy(t => t.FullName)
    .ToArray();

Console.WriteLine($"Types matching pet keywords: {types.Length}");
foreach (var t in types)
    Console.WriteLine($"- {t.FullName}");

static void DumpGuidLikeMembers(Type t)
{
    foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        if (p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?))
            Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");

    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        if (f.FieldType == typeof(Guid) || f.FieldType == typeof(Guid?))
            Console.WriteLine($"  field {f.FieldType.Name} {f.Name}");
}

// Try to locate likely identifiers we can use in the mod.
Type? petKind = asm.GetType("Road.Assets.PetKind", throwOnError: false);
if (petKind is not null && petKind.IsEnum)
{
    Console.WriteLine();
    Console.WriteLine("Enum Road.Assets.PetKind values:");
    foreach (var name in Enum.GetNames(petKind))
        Console.WriteLine($"- {name}");
}

foreach (string fq in new[]
         {
             // Top-level types
             "Road.Assets.PetInformation",
             "Road.Assets.PetKind",

             // Nested types (C# '+' reflection syntax)
             "Road.Assets.RoadLibraryAsset+PetResourceData",
             "Road.Assets.RoadLibraryAsset+PetOutputResourceData",
             "Road.Assets.RoadLibraryAsset+PetsData",
             "Road.Assets.UiSkinAsset+PetUiResource",
         })
{
    var t = asm.GetType(fq, throwOnError: false);
    if (t is null)
        continue;

    Console.WriteLine();
    Console.WriteLine($"Found {fq}");
    Console.WriteLine("Guid-like members:");
    DumpGuidLikeMembers(t);

    Console.WriteLine("Members (public instance):");
    foreach (var m in t.GetMembers(BindingFlags.Instance | BindingFlags.Public)
                 .OrderBy(m => m.MemberType).ThenBy(m => m.Name))
    {
        Console.WriteLine($"  {m.MemberType}: {m.Name}");
    }

    Console.WriteLine("Members (static):");
    foreach (var m in t.GetMembers(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 .OrderBy(m => m.MemberType).ThenBy(m => m.Name))
    {
        if (m.MemberType is MemberTypes.Method)
            continue;
        Console.WriteLine($"  {m.MemberType}: {m.Name}");
    }
}

// Heuristic: locate a static property/field that looks like an ImmutableDictionary<PetKind, PetResourceData>
Console.WriteLine();
Console.WriteLine("Searching for static members containing 'PetResourceData'...");
foreach (var t in asm.GetTypes().OrderBy(t => t.FullName))
{
    if (t.FullName is null)
        continue;

    if (!IsPetRelevant(t.FullName) && !t.FullName.Contains("Pets", StringComparison.OrdinalIgnoreCase))
        continue;

    foreach (var p in t.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
    {
        if (p.PropertyType.FullName?.Contains("PetResourceData", StringComparison.OrdinalIgnoreCase) == true)
            Console.WriteLine($"- {t.FullName}.{p.Name} : {p.PropertyType.FullName}");
    }

    foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
    {
        if (f.FieldType.FullName?.Contains("PetResourceData", StringComparison.OrdinalIgnoreCase) == true)
            Console.WriteLine($"- {t.FullName}.{f.Name} : {f.FieldType.FullName}");
    }
}

// Also try to locate "RoadLibraryAsset" and dump members that mention pets.
var roadLibraryAsset = asm.GetType("Road.Assets.RoadLibraryAsset", throwOnError: false);
if (roadLibraryAsset is not null)
{
    Console.WriteLine();
    Console.WriteLine("Road.Assets.RoadLibraryAsset static members (pet-related):");
    foreach (var p in roadLibraryAsset.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 .OrderBy(p => p.Name))
    {
        if (p.Name.Contains("Pet", StringComparison.OrdinalIgnoreCase)
            || p.PropertyType.FullName?.Contains("Pet", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine($"- prop {p.Name} : {p.PropertyType.FullName}");
        }
    }

    foreach (var f in roadLibraryAsset.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                 .OrderBy(f => f.Name))
    {
        if (f.Name.Contains("Pet", StringComparison.OrdinalIgnoreCase)
            || f.FieldType.FullName?.Contains("Pet", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine($"- field {f.Name} : {f.FieldType.FullName}");
        }
    }
}

// Dump methods that look like they perform pet adoption/unlock/spawn.
Console.WriteLine();
Console.WriteLine("Searching for methods containing keywords: Adopt/Unlock/Pet...");
string[] methodKeywords = ["Adopt", "Unlock", "Pet"];
foreach (var t in asm.GetTypes().OrderBy(t => t.FullName))
{
    if (t.FullName is null)
        continue;

    foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
    {
        if (methodKeywords.Any(k => m.Name.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            // Keep output smaller by focusing on likely namespaces.
            if (!t.FullName.StartsWith("Road.", StringComparison.Ordinal))
                continue;

            var parms = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
            Console.WriteLine($"- {t.FullName}.{m.Name}({parms})");
        }
    }
}
