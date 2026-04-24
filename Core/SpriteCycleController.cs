using System.Collections.Immutable;
using Bang.Components;
using Bang.Entities;
using Murder;
using Murder.Assets.Graphics;
using Murder.Core;
using Murder.Components;

namespace DevTools.Core;

/// <summary>
/// Handles "cycle through animations" behavior for sprites spawned by the Sprite tab.
/// 
/// Murder already animates sprites once a <see cref="SpriteComponent"/> is playing an animation.
/// This controller just advances to the next animation whenever the current one completes.
/// </summary>
public static class SpriteCycleController
{
    /// <summary>
    /// Marker component added to spawned entities so we can find them.
    /// </summary>
    private readonly struct SpriteCycleComponent : IComponent
    {
        public readonly Guid SpriteGuid;
        public readonly int NextIndex;
        public readonly float Time;
        public readonly float Duration;

        public SpriteCycleComponent(Guid spriteGuid, int nextIndex, float time, float duration)
        {
            SpriteGuid = spriteGuid;
            NextIndex = nextIndex;
            Time = time;
            Duration = duration;
        }
    }

    private static readonly Type[] _query = [typeof(SpriteCycleComponent), typeof(SpriteComponent)];

    /// <summary>
    /// Update sprite cycling for all spawned sprite entities.
    /// </summary>
    public static void Update(MonoWorld world, float deltaSeconds)
    {
        var entities = world.GetEntitiesWith(_query);
        if (entities.Length == 0)
            return;

        foreach (var e in entities)
        {
            if (!TryGetCycleState(e, out var cycle, out var sprite))
                continue;

            // Some animations are configured to loop forever, and in that case Murder may never
            // mark the entity with AnimationCompleteComponent.
            //
            // So: we advance either when we see the completion component OR when we've exceeded
            // the animation's duration.
            bool completed = e.HasComponent(typeof(AnimationCompleteComponent));
            float newTime = cycle.Time + MathF.Max(0, deltaSeconds);
            bool timeUp = cycle.Duration > 0 && newTime >= cycle.Duration;

            if (!completed && !timeUp)
            {
                // Just tick time.
                e.AddOrReplaceComponent(new SpriteCycleComponent(cycle.SpriteGuid, cycle.NextIndex, newTime, cycle.Duration), typeof(SpriteCycleComponent));
                continue;
            }

            // Decide next animation id.
            var next = GetNextAnimationId(cycle.SpriteGuid, cycle.NextIndex);
            if (next is null)
                continue;

            // Update sprite component to play the next animation.
            // IMPORTANT: clear any queued NextAnimations so we deterministically play the one we choose.
            // Also explicitly provide the sprite guid so Murder doesn't keep playing a previous sprite's animation.
            sprite = sprite.ClearAllNext().Play(ImmutableArray.Create(next), (Guid?)cycle.SpriteGuid);
            e.AddOrReplaceComponent(sprite, typeof(SpriteComponent));

            // Clear completion flag so we don't advance multiple times.
            try { e.RemoveComponent(typeof(AnimationCompleteComponent)); } catch { /* ignore */ }

            // Update cycle state.
            float nextDuration = GetAnimationDurationSeconds(cycle.SpriteGuid, next);
            e.AddOrReplaceComponent(new SpriteCycleComponent(cycle.SpriteGuid, cycle.NextIndex + 1, time: 0, duration: nextDuration), typeof(SpriteCycleComponent));
        }
    }

    /// <summary>
    /// Adds the cycle marker and starts the first animation on the entity.
    /// Returns false if the sprite asset has no animations to play.
    /// </summary>
    public static bool TryInitializeCycle(Entity entity, Guid spriteGuid)
    {
        var first = GetNextAnimationId(spriteGuid, 0);
        if (first is null)
            return false;

        // Ensure the sprite plays our first animation.
        if (entity.TryGetComponent(typeof(SpriteComponent), out IComponent? spriteComp) && spriteComp is SpriteComponent sprite)
        {
            sprite = sprite.ClearAllNext().Play(ImmutableArray.Create(first), (Guid?)spriteGuid);
            entity.AddOrReplaceComponent(sprite, typeof(SpriteComponent));
        }
        else
        {
            entity.AddOrReplaceComponent(new SpriteComponent(new Murder.Core.Portrait(spriteGuid, first)), typeof(SpriteComponent));
        }

        float firstDuration = GetAnimationDurationSeconds(spriteGuid, first);
        entity.AddOrReplaceComponent(new SpriteCycleComponent(spriteGuid, 1, time: 0, duration: firstDuration), typeof(SpriteCycleComponent));
        return true;
    }

    private static bool TryGetCycleState(Entity e, out SpriteCycleComponent cycle, out SpriteComponent sprite)
    {
        cycle = default;
        sprite = default;

        if (!e.TryGetComponent(typeof(SpriteCycleComponent), out IComponent? cycleComp) || cycleComp is not SpriteCycleComponent c)
            return false;
        if (!e.TryGetComponent(typeof(SpriteComponent), out IComponent? spriteComp) || spriteComp is not SpriteComponent s)
            return false;

        cycle = c;
        sprite = s;
        return true;
    }

    private static string? GetNextAnimationId(Guid spriteGuid, int index)
    {
        try
        {
            var asset = Game.Data.TryGetAsset(spriteGuid) as SpriteAsset;
            if (asset is null)
                return null;

            if (asset.Animations is { Count: > 0 })
            {
                // Filter invalid animation keys and keep a stable, deterministic order.
                // (Some sprite assets can contain empty keys depending on how they were authored.)
                var names = asset.Animations.Keys
                    .Where(k => !string.IsNullOrWhiteSpace(k))
                    .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (names.Length == 0)
                    return null;

                return names[index % names.Length];
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static float GetAnimationDurationSeconds(Guid spriteGuid, string animationId)
    {
        try
        {
            var asset = Game.Data.TryGetAsset(spriteGuid) as SpriteAsset;
            if (asset is null)
                return 0;

            if (!asset.Animations.TryGetValue(animationId, out var anim))
                return 0;

            // Prefer the cached animation duration.
            if (anim.AnimationDuration > 0)
                return anim.AnimationDuration;

            // Fall back to summing frame durations.
            float sum = 0;
            if (!anim.FramesDuration.IsDefaultOrEmpty)
            {
                foreach (var d in anim.FramesDuration)
                    sum += d;
            }

            // Final fallback: if the sprite has an animation but no durations, pick a short default.
            return sum > 0 ? sum : 0.2f;
        }
        catch
        {
            return 0.2f;
        }
    }
}
