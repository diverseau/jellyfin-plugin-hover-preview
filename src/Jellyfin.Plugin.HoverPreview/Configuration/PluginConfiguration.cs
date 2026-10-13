using System;
using Jellyfin.Plugin.HoverPreview.Clips;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.HoverPreview.Configuration;

/// <summary>Admin settings. Values are clamped when read, so a hand-edited XML file can't break anything.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether hover cards are shown at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets how long the pointer must rest on a card before it pops out.</summary>
    public int HoverDelayMs { get; set; } = 500;

    /// <summary>Gets or sets a value indicating whether the card plays a clip (otherwise still image only).</summary>
    public bool EnableVideo { get; set; } = true;

    /// <summary>Gets or sets the clip length in seconds.</summary>
    public int ClipDurationSeconds { get; set; } = 60;

    /// <summary>Gets or sets a value indicating whether clips start muted.</summary>
    public bool StartMuted { get; set; } = true;

    public bool EnableOnHome { get; set; } = true;

    public bool EnableOnLibraries { get; set; } = true;

    public bool EnableOnSearch { get; set; } = true;

    public bool EnableOnDetails { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether browser-compatible files may stream untouched.</summary>
    public bool AllowDirectPlay { get; set; } = true;

    public int MaxDirectPlayBitrateMbps { get; set; } = 20;

    public int TranscodeMaxWidth { get; set; } = 854;

    public int TranscodeBitrateKbps { get; set; } = 1500;

    /// <summary>Gets or sets the transcode width for the big banner clip (Media Bar Card), which is far larger than a card.</summary>
    public int BannerMaxWidth { get; set; } = 1920;

    public int BannerBitrateKbps { get; set; } = 6000;

    public int MovieRangeStartPercent { get; set; } = 15;

    public int MovieRangeEndPercent { get; set; } = 55;

    public int EpisodeRangeStartPercent { get; set; } = 10;

    public int EpisodeRangeEndPercent { get; set; } = 65;

    internal int ClampedDelayMs => Math.Clamp(HoverDelayMs, 100, 3000);

    internal int ClampedClipSeconds => Math.Clamp(ClipDurationSeconds, 20, 180);

    internal ClipOptions ToClipOptions()
    {
        var (movieStart, movieEnd) = Range(MovieRangeStartPercent, MovieRangeEndPercent, 15, 55);
        var (episodeStart, episodeEnd) = Range(EpisodeRangeStartPercent, EpisodeRangeEndPercent, 10, 65);
        return new ClipOptions
        {
            Duration = TimeSpan.FromSeconds(ClampedClipSeconds),
            MovieRangeStart = movieStart,
            MovieRangeEnd = movieEnd,
            EpisodeRangeStart = episodeStart,
            EpisodeRangeEnd = episodeEnd,
        };
    }

    internal StreamOptions ToStreamOptions(PreviewSize size = PreviewSize.Card) => new()
    {
        AllowDirectPlay = AllowDirectPlay,
        MaxDirectBitrate = Math.Clamp(MaxDirectPlayBitrateMbps, 1, 200) * 1_000_000,
        MaxWidth = (size == PreviewSize.Banner ? Math.Clamp(BannerMaxWidth, 640, 3840) : Math.Clamp(TranscodeMaxWidth, 320, 1920)) / 2 * 2,
        VideoBitrate = (size == PreviewSize.Banner ? Math.Clamp(BannerBitrateKbps, 1000, 40000) : Math.Clamp(TranscodeBitrateKbps, 300, 20000)) * 1000,
    };

    /// <summary>A cache key fragment that changes whenever a setting affecting clip plans changes.</summary>
    internal string PlanFingerprint() => string.Join(
        ':',
        ClampedClipSeconds,
        MovieRangeStartPercent,
        MovieRangeEndPercent,
        EpisodeRangeStartPercent,
        EpisodeRangeEndPercent,
        AllowDirectPlay,
        MaxDirectPlayBitrateMbps,
        TranscodeMaxWidth,
        TranscodeBitrateKbps,
        BannerMaxWidth,
        BannerBitrateKbps);

    private static (double Start, double End) Range(int start, int end, int defaultStart, int defaultEnd)
    {
        start = Math.Clamp(start, 0, 95);
        end = Math.Clamp(end, 5, 100);
        if (end - start < 5)
        {
            (start, end) = (defaultStart, defaultEnd);
        }

        return (start / 100.0, end / 100.0);
    }
}
