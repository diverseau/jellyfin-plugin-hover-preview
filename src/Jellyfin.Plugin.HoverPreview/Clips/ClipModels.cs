using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.HoverPreview.Clips;

/// <summary>
/// Kind of a marked section of a video (mirrors Jellyfin's media segment types, kept local so the
/// picker has no dependency on server assemblies and can be unit tested in isolation).
/// </summary>
public enum SegmentKind
{
    Unknown = 0,
    Intro,
    Recap,
    Outro,
    Commercial,
    Preview,
}

/// <summary>A marked section of a video.</summary>
/// <param name="Kind">Segment kind.</param>
/// <param name="StartTicks">Start, in 100 ns ticks.</param>
/// <param name="EndTicks">End, in 100 ns ticks.</param>
public readonly record struct Segment(SegmentKind Kind, long StartTicks, long EndTicks);

/// <summary>Everything the picker knows about one playable video.</summary>
/// <param name="ItemId">Jellyfin item id of the video.</param>
/// <param name="RuntimeTicks">Runtime in ticks.</param>
/// <param name="ChapterStartTicks">Chapter start positions in ticks (any order).</param>
/// <param name="Segments">Media segments (intro, credits, ...), possibly empty.</param>
public sealed record ClipSource(
    Guid ItemId,
    long RuntimeTicks,
    IReadOnlyList<long> ChapterStartTicks,
    IReadOnlyList<Segment> Segments);

/// <summary>A chosen preview window inside one video.</summary>
/// <param name="ItemId">Item the window belongs to (for shows this is an episode, not the series).</param>
/// <param name="StartTicks">Window start in ticks.</param>
/// <param name="DurationTicks">Window length in ticks.</param>
/// <param name="Strategy">Which heuristic produced it (diagnostics only).</param>
public sealed record ClipWindow(Guid ItemId, long StartTicks, long DurationTicks, string Strategy)
{
    /// <summary>Gets the window end in ticks.</summary>
    public long EndTicks => StartTicks + DurationTicks;
}

/// <summary>Tunables for the picker. All fractions are of the video's runtime.</summary>
public sealed record ClipOptions
{
    /// <summary>Gets the preferred clip length.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the shortest clip worth playing; shorter usable ranges become still-only.</summary>
    public TimeSpan MinDuration { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the shortest video that gets a clip at all.</summary>
    public TimeSpan MinRuntime { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Gets the earliest point a movie clip may start.</summary>
    public double MovieRangeStart { get; init; } = 0.15;

    /// <summary>Gets the latest point a movie clip may end (spoiler guard).</summary>
    public double MovieRangeEnd { get; init; } = 0.55;

    /// <summary>Gets the earliest point an episode clip may start when no intro marker exists.</summary>
    public double EpisodeRangeStart { get; init; } = 0.10;

    /// <summary>Gets the latest point an episode clip may end.</summary>
    public double EpisodeRangeEnd { get; init; } = 0.65;

    /// <summary>Gets how many clips a show's pool holds.</summary>
    public int SeriesPoolSize { get; init; } = 3;
}
