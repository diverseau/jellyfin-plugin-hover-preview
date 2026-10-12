using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.HoverPreview.Clips;

/// <summary>
/// Chooses spoiler-safe preview windows. Pure and deterministic: the same inputs always give the
/// same windows, so results can be cached and tested.
/// </summary>
public sealed class ClipPicker
{
    /// <summary>
    /// Bump whenever the selection logic changes so cached plans are recomputed.
    /// </summary>
    public const int HeuristicVersion = 2;

    private static readonly long ColdOpenMinIntroStart = TimeSpan.FromSeconds(60).Ticks;

    private readonly IReadOnlyList<IClipHeuristic> _ladder;
    private readonly ClipOptions _options;

    /// <summary>Initializes a new instance of the <see cref="ClipPicker"/> class.</summary>
    /// <param name="options">Tunables.</param>
    /// <param name="ladder">Heuristics in priority order; defaults to chapters then position grid.</param>
    public ClipPicker(ClipOptions options, IReadOnlyList<IClipHeuristic>? ladder = null)
    {
        _options = options;
        _ladder = ladder ?? new IClipHeuristic[] { new ChapterHeuristic(), new PositionHeuristic() };
    }

    /// <summary>Picks the single preview window for a movie (or any standalone video).</summary>
    /// <param name="movie">The movie.</param>
    /// <returns>The window, or null when the movie should be still-only.</returns>
    public ClipWindow? PickMovie(ClipSource movie)
    {
        return PickWindows(movie, _options.MovieRangeStart, _options.MovieRangeEnd, 1, allowColdOpen: false)
            .FirstOrDefault();
    }

    /// <summary>
    /// Picks a show's clip pool from its first episodes (normally S1E1 and S1E2). Every card of the
    /// show (series, season, any episode) plays from this pool, so nothing past the start of the show
    /// is ever shown.
    /// </summary>
    /// <param name="firstEpisodes">The show's earliest episodes in airing order; only the first two are used.</param>
    /// <returns>Up to <see cref="ClipOptions.SeriesPoolSize"/> non-overlapping windows.</returns>
    public IReadOnlyList<ClipWindow> PickSeriesPool(IReadOnlyList<ClipSource> firstEpisodes)
    {
        var poolSize = Math.Max(1, _options.SeriesPoolSize);
        var episodes = firstEpisodes.Take(2).ToList();
        if (episodes.Count == 0)
        {
            return Array.Empty<ClipWindow>();
        }

        // Split the pool across the episodes, first episode gets the remainder (2 + 1 for a pool of 3).
        var quotas = new int[episodes.Count];
        for (var i = 0; i < poolSize; i++)
        {
            quotas[i % episodes.Count]++;
        }

        var perEpisode = new List<ClipWindow>[episodes.Count];
        var shortfall = 0;
        for (var i = 0; i < episodes.Count; i++)
        {
            perEpisode[i] = PickWindows(episodes[i], _options.EpisodeRangeStart, _options.EpisodeRangeEnd, quotas[i], allowColdOpen: true).ToList();
            shortfall += quotas[i] - perEpisode[i].Count;
        }

        // If one episode could not fill its quota (too short, no usable range), let the others make up for it.
        for (var i = 0; i < episodes.Count && shortfall > 0; i++)
        {
            var more = PickWindows(episodes[i], _options.EpisodeRangeStart, _options.EpisodeRangeEnd, perEpisode[i].Count + shortfall, allowColdOpen: true);
            var added = more.Where(w => !perEpisode[i].Any(existing => Overlaps(existing, w))).Take(shortfall).ToList();
            perEpisode[i].AddRange(added);
            shortfall -= added.Count;
        }

        return perEpisode.SelectMany(w => w).Take(poolSize).ToList();
    }

    /// <summary>Places up to <paramref name="count"/> non-overlapping windows in one video.</summary>
    /// <param name="source">The video.</param>
    /// <param name="rangeStartFraction">Earliest start as a fraction of runtime (ignored after an intro marker).</param>
    /// <param name="rangeEndFraction">Latest end as a fraction of runtime.</param>
    /// <param name="count">How many windows to return at most.</param>
    /// <param name="allowColdOpen">Whether a cold open before a marked intro may be used.</param>
    /// <returns>Windows ordered by start.</returns>
    internal IReadOnlyList<ClipWindow> PickWindows(ClipSource source, double rangeStartFraction, double rangeEndFraction, int count, bool allowColdOpen)
    {
        var runtime = source.RuntimeTicks;
        if (count <= 0 || runtime < _options.MinRuntime.Ticks)
        {
            return Array.Empty<ClipWindow>();
        }

        var minDuration = _options.MinDuration.Ticks;
        var exclusions = BuildExclusions(source);
        var result = new List<ClipWindow>();

        if (allowColdOpen && TryColdOpen(source, out var coldOpen))
        {
            result.Add(coldOpen);
        }

        // An intro marker tells us exactly where the episode proper begins; otherwise use the fraction.
        var rangeStart = (long)(runtime * Math.Clamp(rangeStartFraction, 0, 1));
        var intro = source.Segments.Where(s => s.Kind == SegmentKind.Intro).OrderBy(s => s.StartTicks).FirstOrDefault();
        if (intro.Kind == SegmentKind.Intro && intro.EndTicks < runtime / 2)
        {
            rangeStart = intro.EndTicks;
        }

        var rangeEnd = (long)(runtime * Math.Clamp(rangeEndFraction, 0, 1));

        // Shrink the clip if the safe range is short, but never below the minimum.
        var duration = Math.Min(_options.Duration.Ticks, rangeEnd - rangeStart);
        if (duration < minDuration)
        {
            return result;
        }

        var context = new ClipContext(source, rangeStart, rangeEnd, duration);
        foreach (var heuristic in _ladder)
        {
            var candidates = heuristic.ProposeStarts(context)
                .Where(start => start >= rangeStart && start + duration <= rangeEnd)
                .Where(start => !exclusions.Any(ex => start < ex.EndTicks && start + duration > ex.StartTicks))
                .Distinct()
                .OrderBy(start => start)
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            Choose(source.ItemId, candidates, rangeStart, rangeEnd, duration, count - result.Count, heuristic.Name, result);

            // A heuristic that found too few windows is topped up by the next rung, not left short.
            if (result.Count >= count)
            {
                break;
            }
        }

        return result.OrderBy(w => w.StartTicks).Take(count).ToList();
    }

    /// <summary>Stable 32-bit FNV-1a hash of an item id and a salt.</summary>
    internal static uint StableHash(Guid id, int salt)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var b in id.ToByteArray())
            {
                hash = (hash ^ b) * 16777619;
            }

            for (var i = 0; i < 4; i++)
            {
                hash = (hash ^ (byte)(salt >> (8 * i))) * 16777619;
            }

            return hash;
        }
    }

    private static bool Overlaps(ClipWindow a, ClipWindow b) => a.StartTicks < b.EndTicks && b.StartTicks < a.EndTicks;

    /// <summary>
    /// Spreads picks across the range: the range is cut into equal buckets and one candidate is taken
    /// from each, chosen by a stable hash of the item id so neighbouring cards don't all start at the
    /// same relative position.
    /// </summary>
    private static void Choose(Guid itemId, List<long> candidates, long rangeStart, long rangeEnd, long duration, int count, string strategy, List<ClipWindow> result)
    {
        if (count <= 0)
        {
            return;
        }

        var existing = result.ToList();
        var bucketSize = Math.Max(1, (rangeEnd - rangeStart) / count);
        for (var bucket = 0; bucket < count; bucket++)
        {
            var lo = rangeStart + (bucket * bucketSize);
            var hi = bucket == count - 1 ? long.MaxValue : lo + bucketSize;
            var inBucket = candidates.Where(c => c >= lo && c < hi).ToList();
            TryAdd(itemId, inBucket, bucket, duration, strategy, result);
        }

        // Buckets with no usable candidate: fill from anywhere that doesn't overlap.
        var salt = count;
        while (result.Count - existing.Count < count)
        {
            var before = result.Count;
            TryAdd(itemId, candidates, salt++, duration, strategy, result);
            if (result.Count == before)
            {
                break;
            }
        }

        // In a tight range a random pick can block the others; packing left to right may fit more.
        if (result.Count - existing.Count < count)
        {
            var packed = existing.ToList();
            foreach (var start in candidates)
            {
                var window = new ClipWindow(itemId, start, duration, strategy);
                if (packed.Count - existing.Count < count && !packed.Any(w => Overlaps(w, window)))
                {
                    packed.Add(window);
                }
            }

            if (packed.Count > result.Count)
            {
                result.Clear();
                result.AddRange(packed);
            }
        }
    }

    private static void TryAdd(Guid itemId, List<long> candidates, int salt, long duration, string strategy, List<ClipWindow> result)
    {
        var free = candidates
            .Select(start => new ClipWindow(itemId, start, duration, strategy))
            .Where(w => !result.Any(existing => Overlaps(existing, w)))
            .ToList();
        if (free.Count > 0)
        {
            result.Add(free[(int)(StableHash(itemId, salt) % (uint)free.Count)]);
        }
    }

    /// <summary>Regions a clip must never touch.</summary>
    private static List<Segment> BuildExclusions(ClipSource source)
    {
        var runtime = source.RuntimeTicks;
        var exclusions = source.Segments
            .Where(s => s.Kind is SegmentKind.Intro or SegmentKind.Recap or SegmentKind.Outro or SegmentKind.Commercial or SegmentKind.Preview)
            .Where(s => s.EndTicks > s.StartTicks)
            .ToList();

        // Without credit markers, assume the last 15% is credits / the ending.
        if (!exclusions.Any(s => s.Kind == SegmentKind.Outro))
        {
            exclusions.Add(new Segment(SegmentKind.Outro, (long)(runtime * 0.85), runtime));
        }

        // Without opening markers, skip the first 3% (logos, cold-open titles).
        if (!exclusions.Any(s => s.Kind is SegmentKind.Intro or SegmentKind.Recap))
        {
            exclusions.Add(new Segment(SegmentKind.Intro, 0, (long)(runtime * 0.03)));
        }

        return exclusions;
    }

    /// <summary>
    /// When a show's title sequence starts after a minute or more, everything before it is the cold
    /// open, which is written to hook the viewer: ideal preview material.
    /// </summary>
    private bool TryColdOpen(ClipSource source, out ClipWindow window)
    {
        window = null!;
        var intro = source.Segments.Where(s => s.Kind == SegmentKind.Intro).OrderBy(s => s.StartTicks).FirstOrDefault();
        if (intro.Kind != SegmentKind.Intro || intro.StartTicks < ColdOpenMinIntroStart)
        {
            return false;
        }

        // Skip a "previously on" recap that plays before the cold open.
        var start = source.Segments
            .Where(s => s.Kind is SegmentKind.Recap or SegmentKind.Preview && s.EndTicks <= intro.StartTicks)
            .Select(s => s.EndTicks)
            .DefaultIfEmpty(0)
            .Max();
        var length = Math.Min(_options.Duration.Ticks, intro.StartTicks - start);
        if (length < _options.MinDuration.Ticks)
        {
            return false;
        }

        // Never run into an ad break or "next time on" segment inside the cold open.
        var end = start + length;
        if (source.Segments.Any(s => s.Kind is SegmentKind.Commercial or SegmentKind.Preview && s.StartTicks < end && s.EndTicks > start))
        {
            return false;
        }

        window = new ClipWindow(source.ItemId, start, length, "cold-open");
        return true;
    }
}
