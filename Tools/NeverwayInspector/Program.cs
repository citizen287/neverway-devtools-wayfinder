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

// ---- Map-related introspection -------------------------------------------------
Console.WriteLine();
Console.WriteLine("=== Map-related (RoadSaveData) ===");

Type? roadSaveData = asm.GetType("Road.Assets.RoadSaveData", throwOnError: false);
if (roadSaveData is null)
{
    Console.WriteLine("Road.Assets.RoadSaveData not found.");
}

if (roadSaveData is not null)
{
    static string FormatMethod(MethodInfo m)
    {
        string parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        return $"{m.ReturnType.Name} {m.Name}({parms})";
    }

    Console.WriteLine($"Type: {roadSaveData.FullName}");

    Console.WriteLine("Fields containing 'Map':");
    foreach (var f in roadSaveData.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(f => f.Name.Contains("Map", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(f => f.Name))
    {
        Console.WriteLine($"- {f.FieldType.Name} {f.Name}");
    }

    Console.WriteLine("Fields containing 'Unlocked':");
    foreach (var f in roadSaveData.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(f => f.Name.Contains("Unlocked", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(f => f.Name))
    {
        Console.WriteLine($"- {f.FieldType.Name} {f.Name}");
    }

    Console.WriteLine("Properties containing 'Map':");
    foreach (var p in roadSaveData.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(p => p.Name.Contains("Map", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(p => p.Name))
    {
        Console.WriteLine($"- {p.PropertyType.Name} {p.Name}");
    }

    Console.WriteLine("Properties containing 'Unlocked':");
    foreach (var p in roadSaveData.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(p => p.Name.Contains("Unlocked", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(p => p.Name))
    {
        Console.WriteLine($"- {p.PropertyType.Name} {p.Name}");
    }

    Console.WriteLine("Methods containing 'Map':");
    foreach (var m in roadSaveData.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(m => m.Name.Contains("Map", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(m => m.Name))
    {
        Console.WriteLine($"- {FormatMethod(m)}");
    }

    Console.WriteLine();
    Console.WriteLine("=== Teleporter-related (RoadSaveData) ===");

    Console.WriteLine("Fields containing 'Teleporter':");
    foreach (var f in roadSaveData.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(f => f.Name.Contains("Teleporter", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(f => f.Name))
    {
        Console.WriteLine($"- {f.FieldType.Name} {f.Name}");
    }

    Console.WriteLine("Properties containing 'Teleporter':");
    foreach (var p in roadSaveData.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(p => p.Name.Contains("Teleporter", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(p => p.Name))
    {
        Console.WriteLine($"- {p.PropertyType.Name} {p.Name}");
    }

    Console.WriteLine("Methods containing 'Teleporter':");
    foreach (var m in roadSaveData.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                 .Where(m => m.Name.Contains("Teleporter", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(m => m.Name))
    {
        Console.WriteLine($"- {FormatMethod(m)}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Map-related (BuildingId enum guess) ===");

// BuildingId type may be used by RoadSaveData.UnlockMapBuilding(BuildingId)
Type? buildingId = asm.GetType("Road.Assets.BuildingId", throwOnError: false)
    ?? asm.GetType("Road.BuildingId", throwOnError: false)
    ?? asm.GetTypes().FirstOrDefault(t => string.Equals(t.Name, "BuildingId", StringComparison.Ordinal) && (t.Namespace?.StartsWith("Road", StringComparison.Ordinal) == true));

if (buildingId is null)
{
    Console.WriteLine("BuildingId type not found.");
}
else
{
    Console.WriteLine($"Found: {buildingId.FullName}");
    Console.WriteLine($"IsEnum: {buildingId.IsEnum}");
    if (buildingId.IsEnum)
    {
        foreach (var name in Enum.GetNames(buildingId).Take(200))
            Console.WriteLine($"- {name}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Teleporter-related (MapPlaces enum guess) ===");

Type? mapPlaces = asm.GetType("Road.Core.MapPlaces", throwOnError: false)
    ?? asm.GetTypes().FirstOrDefault(t => string.Equals(t.Name, "MapPlaces", StringComparison.Ordinal) && (t.Namespace?.StartsWith("Road", StringComparison.Ordinal) == true));

if (mapPlaces is null)
{
    Console.WriteLine("MapPlaces type not found.");
}
else
{
    Console.WriteLine($"Found: {mapPlaces.FullName}");
    Console.WriteLine($"IsEnum: {mapPlaces.IsEnum}");
    if (mapPlaces.IsEnum)
    {
        foreach (var name in Enum.GetNames(mapPlaces).Take(300))
            Console.WriteLine($"- {name}");
    }
}
