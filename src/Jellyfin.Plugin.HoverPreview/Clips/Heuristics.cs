using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.HoverPreview.Clips;

/// <summary>Starts clips at chapter boundaries: scene changes chosen by a human editor.</summary>
public sealed class ChapterHeuristic : IClipHeuristic
{
    /// <inheritdoc />
    public string Name => "chapter";

    /// <inheritdoc />
    public IEnumerable<long> ProposeStarts(ClipContext context)
    {
        foreach (var start in context.Source.ChapterStartTicks)
        {
            // A chapter at 0 is just "the start of the file", not a scene boundary.
            if (start > TimeSpan.TicksPerSecond)
            {
                yield return start;
            }
        }
    }
}

/// <summary>Fallback: every point on a 10 second grid inside the allowed range.</summary>
public sealed class PositionHeuristic : IClipHeuristic
{
    private static readonly long Step = TimeSpan.FromSeconds(10).Ticks;

    /// <inheritdoc />
    public string Name => "position";

    /// <inheritdoc />
    public IEnumerable<long> ProposeStarts(ClipContext context)
    {
        var first = ((context.RangeStartTicks + Step - 1) / Step) * Step;
        if (first + context.DurationTicks > context.RangeEndTicks)
        {
            // Range too tight for any grid point: the range start itself is the only option.
            yield return context.RangeStartTicks;
            yield break;
        }

        for (var start = first; start + context.DurationTicks <= context.RangeEndTicks; start += Step)
        {
            yield return start;
        }
    }
}
