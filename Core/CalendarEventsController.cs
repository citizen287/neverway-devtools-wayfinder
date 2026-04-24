using Murder;
using Murder.Core;
using NeverwayMod.DevTools.Core;

namespace DevTools.Core;

/// <summary>
/// Best-effort toggler for the game's "CalendarEvents" system(s).
/// 
/// The actual CalendarEvents type(s) live in the game's assemblies, so we use
/// reflection to locate any system types whose name contains "CalendarEvents".
/// Then we activate/deactivate those systems on the current <see cref="MonoWorld"/>.
/// </summary>
public static class CalendarEventsController
{
    /// <summary>
    /// Desired state (persists across world changes).
    /// </summary>
    public static bool Enabled { get; private set; } = true;

    private static readonly List<Type> _disabledTypes = new();

    private static object? _lastWorld;
    private static string _lastStatus = string.Empty;

    public static string LastStatus => _lastStatus;

    /// <summary>
    /// Sets the desired state and immediately attempts to apply it to the given world.
    /// </summary>
    public static void SetEnabled(MonoWorld world, bool enabled)
    {
        Enabled = enabled;
        ApplyTo(world);
    }

    /// <summary>
    /// Sets the desired state without requiring an active world.
    /// The setting will be applied next time <see cref="Update"/> sees a world.
    /// </summary>
    public static void SetDesiredEnabled(bool enabled)
    {
        Enabled = enabled;
        _lastStatus = enabled
            ? "CalendarEvents will be enabled when a world is active."
            : "CalendarEvents will be disabled when a world is active.";
    }

    /// <summary>
    /// Re-applies the desired state when the active world changes.
    /// Call this once per frame.
    /// </summary>
    public static void Update()
    {
        var world = GameHelper.GetMonoWorld();
        if (world is null)
            return;

        var currentWorld = (object)world;
        if (!ReferenceEquals(currentWorld, _lastWorld))
        {
            _lastWorld = currentWorld;

            // Reset cached toggled types; systems may differ per world.
            _disabledTypes.Clear();

            ApplyTo(world);
        }
    }

    private static void ApplyTo(MonoWorld world)
    {
        try
        {
            if (Enabled)
            {
                Activate(world);
            }
            else
            {
                Deactivate(world);
            }
        }
        catch (Exception ex)
        {
            _lastStatus = $"CalendarEvents error: {ex.Message}";
        }
    }

    private static void Deactivate(MonoWorld world)
    {
        var candidates = FindCandidateTypes();
        if (candidates.Count == 0)
        {
            _lastStatus = "No CalendarEvents types found (reflection).";
            return;
        }

        _disabledTypes.Clear();

        int deactivated = 0;
        foreach (var t in candidates)
        {
            try
            {
                world.DeactivateSystem(t);
                _disabledTypes.Add(t);
                deactivated++;
            }
            catch
            {
                // System may not exist in this world or might already be inactive.
            }
        }

        _lastStatus = deactivated > 0
            ? $"Disabled {deactivated} CalendarEvents system(s)."
            : "CalendarEvents systems not active in this world.";
    }

    private static void Activate(MonoWorld world)
    {
        int activated = 0;

        // If we previously disabled concrete types, re-enable only those.
        if (_disabledTypes.Count > 0)
        {
            foreach (var t in _disabledTypes)
            {
                try
                {
                    world.ActivateSystem(t);
                    activated++;
                }
                catch
                {
                    // ignore
                }
            }

            _disabledTypes.Clear();
            _lastStatus = activated > 0 ? $"Enabled {activated} CalendarEvents system(s)." : "CalendarEvents enable: no-op.";
            return;
        }

        // Otherwise, best-effort: try activating any matching types we can find.
        var candidates = FindCandidateTypes();
        foreach (var t in candidates)
        {
            try
            {
                world.ActivateSystem(t);
                activated++;
            }
            catch
            {
                // ignore
            }
        }

        _lastStatus = activated > 0 ? $"Enabled {activated} CalendarEvents system(s)." : "CalendarEvents enable: no matching systems found.";
    }

    private static List<Type> FindCandidateTypes()
    {
        var list = new List<Type>(capacity: 4);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var t in types)
            {
                // Try to be specific without hard-referencing game types.
                // Common patterns:
                // - Road.Systems.CalendarEvents
                // - Road.Systems.CalendarEventsSystem
                // - ...CalendarEvents...
                if (t.FullName is null)
                    continue;

                if (!t.FullName.Contains("CalendarEvents", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Heuristic: prefer system-like types (namespace contains Systems).
                if (t.Namespace is not null && t.Namespace.Contains("Systems", StringComparison.OrdinalIgnoreCase))
                    list.Add(t);
            }
        }

        // Deduplicate by full name.
        return list
            .GroupBy(t => t.FullName)
            .Select(g => g.First())
            .OrderBy(t => t.FullName)
            .ToList();
    }
}
