using Murder;
using Murder.Assets.Graphics;
using Murder.Data;

namespace NeverwayMod.DevTools.Core;

/// <summary>
/// Runtime-discovered catalog of sprite assets.
/// 
/// Enumerates all <see cref="SpriteAsset"/> from the game's asset database.
/// Cached to avoid scanning the asset database every ImGui frame.
/// </summary>
internal static class SpriteCatalog
{
    private static (Guid guid, string name)[]? _cache;
    private static float _cacheTime;

    public static (Guid guid, string name)[] GetAllSpritesCached(float refreshSeconds = 10f)
    {
        float now = Game.Now;
        if (_cache is not null && now - _cacheTime < refreshSeconds)
            return _cache;

        _cache = GetAllSpritesUncached();
        _cacheTime = now;
        return _cache;
    }

    private static (Guid guid, string name)[] GetAllSpritesUncached()
    {
        try
        {
            var all = Game.Data.FilterAllAssets(typeof(SpriteAsset));
            var list = new List<(Guid guid, string name)>(all.Count);

            foreach (var kv in all)
            {
                string? name = kv.Value?.Name;
                if (string.IsNullOrWhiteSpace(name))
                    name = kv.Key.ToString()[..8];

                list.Add((kv.Key, name));
            }

            list.Sort(static (a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            return list.ToArray();
        }
        catch
        {
            return [];
        }
    }
}
