using System.Reflection;

namespace NeverwayMod.DevTools.Core;

/// <summary>
/// Runtime-discovered catalog of pets (PetKind enum values).
///
/// This is resolved by reflection so it keeps working even if the game's namespaces move.
/// </summary>
internal static class PetCatalog
{
    private static (object value, string name)[]? _cache;

    // Cache for a while; pet kinds shouldn't change during runtime.
    private static float _cacheTime;

    public static (object value, string name)[] GetAllPetKindsCached(float refreshSeconds = 30f)
    {
        float now = Murder.Game.Now;
        if (_cache is not null && now - _cacheTime < refreshSeconds)
            return _cache;

        _cache = GetAllPetKindsUncached();
        _cacheTime = now;
        return _cache;
    }

    public static Type? ResolvePetKindType()
    {
        const string fqName = "Road.Assets.PetKind";

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fqName, throwOnError: false, ignoreCase: false);
                if (t is not null)
                    return t;
            }
            catch
            {
                // ignore
            }
        }

        // Fallback: try to find any enum type named PetKind.
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
            catch { continue; }

            foreach (var t in types)
            {
                if (t.IsEnum && string.Equals(t.Name, "PetKind", StringComparison.Ordinal))
                    return t;
            }
        }

        return null;
    }

    private static (object value, string name)[] GetAllPetKindsUncached()
    {
        var petKindType = ResolvePetKindType();
        if (petKindType is null || !petKindType.IsEnum)
            return [];

        var values = Enum.GetValues(petKindType);
        var list = new List<(object value, string name)>(values.Length);
        foreach (var v in values)
        {
            if (v is null)
                continue;

            // Skip "None" if present.
            string name = Enum.GetName(petKindType, v) ?? v.ToString() ?? "?";
            if (string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
                continue;

            list.Add((v, name));
        }

        list.Sort(static (a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        return list.ToArray();
    }
}
