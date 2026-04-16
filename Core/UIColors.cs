using System.Numerics;

namespace NeverwayMod.DevTools.Core;

internal static class UIColors
{
    public static readonly Vector4 Error = new(1f, 0.5f, 0.5f, 1f);
    public static readonly Vector4 Active = new(0.2f, 1f, 0.2f, 1f);
    public static readonly Vector4 Inactive = new(0.5f, 0.5f, 0.5f, 1f);

    // Generic text / status colors used by panels.
    public static readonly Vector4 Text = new(1f, 1f, 1f, 1f);
    public static readonly Vector4 Success = new(0.35f, 1f, 0.35f, 1f);
    public static readonly Vector4 Warning = new(1f, 0.85f, 0.3f, 1f);
}
