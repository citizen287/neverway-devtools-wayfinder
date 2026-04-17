using Bang;
using DevTools.Core;
using ImGuiNET;
using Murder.Core;
using Road.Services;
using NeverwayMod.DevTools.Core;

namespace DevTools.UI;

/// <summary>
/// Item giver panel, adapted from the old SpawnMod terminal UI (<c>SpawnMod/ItemModule.cs</c>).
///
/// This panel used to load <c>items.json</c> from embedded resources. That list can become stale,
/// so we now enumerate all <c>Road.Assets.ItemAsset</c> at runtime.
/// </summary>
public static class ItemsPanel
{
    private static bool _loaded;
    private static List<(string Name, Guid Guid)> _all = [];
    private static List<(string Name, Guid Guid)> _filtered = [];

    private static string _search = string.Empty;
    private static int _selected;
    private static int _quantity = 1;

    public static void Render()
    {
        EnsureLoaded();

        var world = GameHelper.GetWorld();
        if (world is not MonoWorld monoWorld)
        {
            ImGui.TextColored(UIColors.Error, "No active MonoWorld.");
            return;
        }

        ImGui.TextDisabled($"Loaded: {_all.Count} items");

        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##item_search", "Search items...", ref _search, 128))
            Filter();

        ImGui.Separator();

        // List
        var listHeight = ImGui.GetTextLineHeightWithSpacing() * 16;
        if (ImGui.BeginChild("ItemsList", new System.Numerics.Vector2(0, listHeight), ImGuiChildFlags.None))
        {
            for (int i = 0; i < _filtered.Count; i++)
            {
                bool isSelected = i == _selected;
                if (ImGui.Selectable(_filtered[i].Name, isSelected))
                    _selected = i;
            }
        }
        ImGui.EndChild();

        if (_filtered.Count > 0)
            _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        else
            _selected = 0;

        // Actions
        ImGui.Spacing();

        bool canGive = _filtered.Count > 0;
        if (!canGive)
            ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(120);
        ImGui.DragInt("Qty", ref _quantity, 1, 1, 999);
        ImGui.SameLine();
        if (ImGui.Button("Give"))
            TryGiveItem(monoWorld, _filtered[_selected].Guid, _quantity);

        ImGui.SameLine();
        if (ImGui.Button("Copy GUID"))
        {
            try { ImGui.SetClipboardText(_filtered[_selected].Guid.ToString()); }
            catch { }
        }

        if (!canGive)
            ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.TextDisabled("Backend: Road.Services.SaveServices.AddItemToInventoryAndNotify()");
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        try
        {
            _all = LoadItemsFromGameAssets();
            Filter();
        }
        catch (Exception ex)
        {
            DevToolsMod.LogError($"ItemsPanel failed to enumerate item assets: {ex.Message}");
            _all = [];
            _filtered = [];
        }
    }

    private static List<(string Name, Guid Guid)> LoadItemsFromGameAssets()
    {
        // Cache is handled by ItemCatalog; this method just adapts it to the panel's tuple type.
        var all = ItemCatalog.GetAllItemsCached(refreshSeconds: 5f);
        var list = new List<(string Name, Guid Guid)>(capacity: all.Length);
        foreach (var (guid, name) in all)
            list.Add((name, guid));
        return list;
    }

    private static void Filter()
    {
        _filtered = string.IsNullOrWhiteSpace(_search)
            ? new List<(string Name, Guid Guid)>(_all)
            : _all.Where(e => e.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_filtered.Count > 0)
            _selected = Math.Clamp(_selected, 0, _filtered.Count - 1);
        else
            _selected = 0;
    }

    private static void TryGiveItem(MonoWorld world, Guid itemGuid, int quantity)
    {
        quantity = Math.Clamp(quantity, 1, 999);

        try
        {
            var player = world.TryGetUniqueEntityPlayer();
            if (player is null)
            {
                ConsoleEngine.AddInfo("Items: No player entity found.");
                return;
            }

            var save = SaveServices.GetOrCreateSave();
            var info = new ItemServices.AcquiredItemInformation(itemGuid)
            {
                Quantity = quantity,
                Target = player
            };

            save.AddItemToInventoryAndNotify(world, info);
            ConsoleEngine.AddInfo($"Gave item x{quantity}: {itemGuid}");
        }
        catch (Exception ex)
        {
            ConsoleEngine.AddInfo($"Failed to give item: {ex.Message}");
        }
    }
}
