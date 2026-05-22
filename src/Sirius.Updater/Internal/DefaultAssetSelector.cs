using System;

namespace Sirius.Updater.Internal;

/// <summary>
/// Default <see cref="UpdateAsset"/> picker used when
/// <see cref="SiriusUpdaterOptions.AssetSelector"/> is not set.
///
/// Strategy:
/// <list type="number">
///   <item>Render <see cref="SiriusUpdaterOptions.AssetNamePattern"/>
///         with <c>{name}</c>, <c>{version}</c>, <c>{arch}</c>; pick the
///         release asset whose name matches case-insensitively.</item>
///   <item>If no exact match, fall back to the first asset whose name
///         ends in <c>.msix</c> or <c>.msixbundle</c>.</item>
///   <item>If nothing matches, return null and let the caller report
///         <c>ASSET_NOT_FOUND</c>.</item>
/// </list>
/// </summary>
internal static class DefaultAssetSelector
{
    public static UpdateAsset? Select(UpdateAssetSelectionContext ctx, string pattern)
    {
        var rendered = pattern
            .Replace("{name}",    ctx.AssetBaseName, StringComparison.Ordinal)
            .Replace("{version}", ctx.Release.Version, StringComparison.Ordinal)
            .Replace("{arch}",    ctx.Architecture, StringComparison.Ordinal);

        foreach (var a in ctx.Release.Assets)
        {
            if (string.Equals(a.Name, rendered, StringComparison.OrdinalIgnoreCase))
                return a;
        }
        foreach (var a in ctx.Release.Assets)
        {
            if (a.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ||
                a.Name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase))
            {
                return a;
            }
        }
        return null;
    }
}
