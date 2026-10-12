using System.Collections.Generic;

namespace Jellyfin.Plugin.HoverPreview.Clips;

/// <summary>
/// One rung of the clip-selection ladder. Each heuristic proposes candidate start positions for a
/// video inside the allowed range; <see cref="ClipPicker"/> asks them in order and stops at the first
/// that proposes anything. New, smarter heuristics (e.g. trickplay motion scoring) slot in here.
/// </summary>
public interface IClipHeuristic
{
    /// <summary>Gets the name reported in <see cref="ClipWindow.Strategy"/>.</summary>
    string Name { get; }

    /// <summary>Proposes candidate window starts. Candidates are validated by the caller.</summary>
    /// <param name="context">The video and the range the window must fit in.</param>
    /// <returns>Candidate start positions in ticks, any order.</returns>
    IEnumerable<long> ProposeStarts(ClipContext context);
}

/// <summary>Input handed to each heuristic.</summary>
/// <param name="Source">The video.</param>
/// <param name="RangeStartTicks">Earliest allowed window start.</param>
/// <param name="RangeEndTicks">Latest allowed window end.</param>
/// <param name="DurationTicks">Window length being placed.</param>
public sealed record ClipContext(ClipSource Source, long RangeStartTicks, long RangeEndTicks, long DurationTicks);
