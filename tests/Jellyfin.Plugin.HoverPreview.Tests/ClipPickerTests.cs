using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.HoverPreview.Clips;
using Xunit;

namespace Jellyfin.Plugin.HoverPreview.Tests;

public class ClipPickerTests
{
    private static readonly ClipOptions Options = new();

    private static long Seconds(double s) => (long)(s * TimeSpan.TicksPerSecond);

    private static long Minutes(double m) => Seconds(m * 60);

    private static ClipSource Video(double runtimeMinutes, IEnumerable<double>? chapterMinutes = null, IEnumerable<Segment>? segments = null, Guid? id = null)
        => new(
            id ?? Guid.NewGuid(),
            Minutes(runtimeMinutes),
            (chapterMinutes ?? Array.Empty<double>()).Select(Minutes).ToList(),
            (segments ?? Array.Empty<Segment>()).ToList());

    [Fact]
    public void Movie_window_stays_inside_spoiler_safe_range()
    {
        var picker = new ClipPicker(Options);
        for (var i = 0; i < 200; i++)
        {
            var movie = Video(120);
            var window = picker.PickMovie(movie);

            Assert.NotNull(window);
            Assert.True(window!.StartTicks >= Minutes(120 * 0.15));
            Assert.True(window.EndTicks <= Minutes(120 * 0.55));
            Assert.Equal(Seconds(60), window.DurationTicks);
        }
    }

    [Fact]
    public void Movie_prefers_chapter_boundaries_in_range()
    {
        var chapters = new[] { 0, 5, 12, 25, 40, 58, 70, 95, 110.0 };
        var movie = Video(120, chapters);

        var window = new ClipPicker(Options).PickMovie(movie)!;

        Assert.Equal("chapter", window.Strategy);
        Assert.Contains(window.StartTicks, new[] { 25, 40, 58.0 }.Select(Minutes));
    }

    [Fact]
    public void Movie_without_chapters_snaps_to_ten_second_grid()
    {
        var window = new ClipPicker(Options).PickMovie(Video(95))!;

        Assert.Equal("position", window.Strategy);
        Assert.Equal(0, window.StartTicks % Seconds(10));
    }

    [Fact]
    public void Same_item_always_gets_same_window()
    {
        var id = Guid.Parse("0d6f8a3c-9e57-4b1a-8c22-5f3e7b9d1a04");
        var picker = new ClipPicker(Options);

        var first = picker.PickMovie(Video(110, new double[] { 20, 30, 40, 50 }, id: id));
        var second = picker.PickMovie(Video(110, new double[] { 20, 30, 40, 50 }, id: id));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Different_items_spread_across_the_range()
    {
        var picker = new ClipPicker(Options);
        var starts = Enumerable.Range(0, 50)
            .Select(_ => picker.PickMovie(Video(100))!.StartTicks)
            .Distinct()
            .Count();

        Assert.True(starts > 10, $"only {starts} distinct start positions");
    }

    [Fact]
    public void Short_videos_are_still_only()
    {
        Assert.Null(new ClipPicker(Options).PickMovie(Video(1.5)));
    }

    [Fact]
    public void Ninety_second_safe_range_shrinks_clip_but_not_below_minimum()
    {
        // 4 minute video: movie range 15%-55% = 36s..132s -> 96 s of room, enough for a 60 s clip.
        var window = new ClipPicker(Options).PickMovie(Video(4));
        Assert.NotNull(window);
        Assert.True(window!.EndTicks <= Minutes(4 * 0.55));

        // A narrow range only fits a shorter clip.
        var narrow = new ClipPicker(Options with { MovieRangeStart = 0.40, MovieRangeEnd = 0.55 });
        var shortWindow = narrow.PickMovie(Video(4));
        Assert.NotNull(shortWindow);
        Assert.True(shortWindow!.DurationTicks < Seconds(60));
        Assert.True(shortWindow.DurationTicks >= Seconds(20));
    }

    [Fact]
    public void Excluded_segments_are_never_used()
    {
        var segments = new[]
        {
            new Segment(SegmentKind.Commercial, Minutes(20), Minutes(45)),
        };
        var picker = new ClipPicker(Options);

        for (var i = 0; i < 100; i++)
        {
            var window = picker.PickMovie(Video(100, null, segments))!;
            Assert.False(window.StartTicks < Minutes(45) && window.EndTicks > Minutes(20));
        }
    }

    [Fact]
    public void Credits_marker_replaces_last_fifteen_percent_rule()
    {
        // Range ends at 95%, outro marked at 97% -> windows may go past 85%.
        var options = Options with { MovieRangeStart = 0.80, MovieRangeEnd = 0.95 };
        var segments = new[] { new Segment(SegmentKind.Outro, Minutes(97), Minutes(100)) };

        var window = new ClipPicker(options).PickMovie(Video(100, null, segments));

        Assert.NotNull(window);
        Assert.True(window!.EndTicks <= Minutes(95));
    }

    [Fact]
    public void Without_credits_marker_last_fifteen_percent_is_excluded()
    {
        var options = Options with { MovieRangeStart = 0.80, MovieRangeEnd = 1.0 };
        var picker = new ClipPicker(options);
        for (var i = 0; i < 50; i++)
        {
            var window = picker.PickMovie(Video(100));
            Assert.NotNull(window);
            Assert.True(window!.EndTicks <= Minutes(85));
        }
    }

    [Fact]
    public void Cold_open_before_a_late_intro_is_used_for_episodes()
    {
        var episode = Video(45, null, new[] { new Segment(SegmentKind.Intro, Minutes(3), Minutes(4)) });

        var pool = new ClipPicker(Options).PickSeriesPool(new[] { episode });

        Assert.Contains(pool, w => w.Strategy == "cold-open" && w.StartTicks == 0 && w.DurationTicks == Seconds(60));
    }

    [Fact]
    public void Cold_open_skips_a_leading_recap()
    {
        var segments = new[]
        {
            new Segment(SegmentKind.Recap, 0, Minutes(1)),
            new Segment(SegmentKind.Intro, Minutes(2.5), Minutes(3.5)),
        };
        var pool = new ClipPicker(Options).PickSeriesPool(new[] { Video(45, null, segments) });

        var cold = Assert.Single(pool, w => w.Strategy == "cold-open");
        Assert.Equal(Minutes(1), cold.StartTicks);
    }

    [Fact]
    public void Early_intro_means_no_cold_open_and_clips_start_after_intro()
    {
        var segments = new[] { new Segment(SegmentKind.Intro, Seconds(10), Seconds(70)) };
        var pool = new ClipPicker(Options).PickSeriesPool(new[] { Video(45, null, segments) });

        Assert.DoesNotContain(pool, w => w.Strategy == "cold-open");
        Assert.All(pool, w => Assert.True(w.StartTicks >= Seconds(70)));
    }

    [Fact]
    public void Series_pool_has_three_clips_from_first_two_episodes()
    {
        var e1 = Video(50, new double[] { 0, 6, 14, 22, 30, 41 });
        var e2 = Video(48, new double[] { 0, 7, 16, 25, 33 });
        var e3 = Video(47);

        var pool = new ClipPicker(Options).PickSeriesPool(new[] { e1, e2, e3 });

        Assert.Equal(3, pool.Count);
        Assert.Equal(2, pool.Count(w => w.ItemId == e1.ItemId));
        Assert.Equal(1, pool.Count(w => w.ItemId == e2.ItemId));
        Assert.DoesNotContain(pool, w => w.ItemId == e3.ItemId);
    }

    [Fact]
    public void Series_pool_windows_do_not_overlap()
    {
        var picker = new ClipPicker(Options);
        for (var i = 0; i < 100; i++)
        {
            var e1 = Video(22);
            var pool = picker.PickSeriesPool(new[] { e1 });
            Assert.Equal(3, pool.Count);
            foreach (var a in pool)
            {
                foreach (var b in pool.Where(b => !ReferenceEquals(a, b)))
                {
                    Assert.False(a.StartTicks < b.EndTicks && b.StartTicks < a.EndTicks, "windows overlap");
                }
            }
        }
    }

    [Fact]
    public void Tight_range_packs_as_many_clips_as_fit()
    {
        // 6 minute episode, chapters every 45 s: range 36 s..234 s fits two 60 s clips only as 45 + 135.
        var chapters = Enumerable.Range(0, 8).Select(i => i * 0.75).ToArray();
        var picker = new ClipPicker(Options);
        for (var i = 0; i < 50; i++)
        {
            var windows = picker.PickWindows(Video(6, chapters), 0.10, 0.65, 2, allowColdOpen: true);
            Assert.Equal(2, windows.Count);
        }
    }

    [Fact]
    public void Too_few_chapter_windows_are_topped_up_from_the_grid()
    {
        // One usable chapter in range; the pool still gets its full three clips.
        var episode = Video(40, new double[] { 0, 10 });

        var pool = new ClipPicker(Options).PickSeriesPool(new[] { episode });

        Assert.Equal(3, pool.Count);
        Assert.Contains(pool, w => w.Strategy == "chapter" && w.StartTicks == Minutes(10));
        Assert.Contains(pool, w => w.Strategy == "position");
    }

    [Fact]
    public void Cold_open_with_an_ad_break_is_not_used()
    {
        var segments = new[]
        {
            new Segment(SegmentKind.Commercial, Seconds(20), Seconds(50)),
            new Segment(SegmentKind.Intro, Minutes(3), Minutes(4)),
        };

        var pool = new ClipPicker(Options).PickSeriesPool(new[] { Video(45, null, segments) });

        Assert.DoesNotContain(pool, w => w.Strategy == "cold-open");
    }

    [Fact]
    public void Series_pool_makes_up_for_a_short_episode()
    {
        var e1 = Video(2); // too short for any clip
        var e2 = Video(40);

        var pool = new ClipPicker(Options).PickSeriesPool(new[] { e1, e2 });

        Assert.Equal(3, pool.Count);
        Assert.All(pool, w => Assert.Equal(e2.ItemId, w.ItemId));
    }

    [Fact]
    public void Series_pool_is_empty_without_episodes()
    {
        Assert.Empty(new ClipPicker(Options).PickSeriesPool(Array.Empty<ClipSource>()));
    }

    [Fact]
    public void Episode_windows_respect_episode_range()
    {
        var picker = new ClipPicker(Options);
        for (var i = 0; i < 100; i++)
        {
            var pool = picker.PickSeriesPool(new[] { Video(44), Video(44) });
            Assert.All(pool, w =>
            {
                Assert.True(w.StartTicks >= Minutes(44 * 0.10));
                Assert.True(w.EndTicks <= Minutes(44 * 0.65));
            });
        }
    }

    [Fact]
    public void Custom_heuristic_ladder_is_used_in_order()
    {
        var picker = new ClipPicker(Options, new IClipHeuristic[] { new FixedHeuristic(Minutes(30)), new PositionHeuristic() });

        var window = picker.PickMovie(Video(100))!;

        Assert.Equal("fixed", window.Strategy);
        Assert.Equal(Minutes(30), window.StartTicks);
    }

    [Fact]
    public void Stable_hash_is_stable()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(ClipPicker.StableHash(id, 3), ClipPicker.StableHash(id, 3));
        Assert.NotEqual(ClipPicker.StableHash(id, 3), ClipPicker.StableHash(id, 4));
    }

    private sealed class FixedHeuristic : IClipHeuristic
    {
        private readonly long _start;

        public FixedHeuristic(long start) => _start = start;

        public string Name => "fixed";

        public IEnumerable<long> ProposeStarts(ClipContext context)
        {
            yield return _start;
        }
    }
}
