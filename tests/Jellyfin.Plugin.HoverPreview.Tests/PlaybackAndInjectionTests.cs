using System;
using Jellyfin.Plugin.HoverPreview.Clips;
using Jellyfin.Plugin.HoverPreview.Configuration;
using Jellyfin.Plugin.HoverPreview.Injection;
using Xunit;

namespace Jellyfin.Plugin.HoverPreview.Tests;

public class PlaybackPlannerTests
{
    private static readonly Guid Id = Guid.Parse("7e440cc9-b5f0-a602-73b9-b83091939842");
    private static readonly StreamOptions Options = new();

    private static SourceTraits Mp4() => new()
    {
        MediaSourceId = "7e440cc9b5f0a60273b9b83091939842",
        Container = "mov,mp4,m4a,3gp,3g2,mj2",
        VideoCodec = "h264",
        VideoBitDepth = 8,
        Width = 1920,
        Height = 1080,
        AudioCodec = "aac",
        Bitrate = 8_000_000,
    };

    [Fact]
    public void H264_aac_mp4_is_direct_playable()
    {
        var plan = PlaybackPlanner.Plan(Id, 123, Mp4(), Options);

        Assert.Equal("/Videos/7e440cc9b5f0a60273b9b83091939842/stream?static=true&mediaSourceId=7e440cc9b5f0a60273b9b83091939842", plan.DirectPath);
        Assert.Equal("video/mp4; codecs=\"avc1.640028,mp4a.40.2\"", plan.DirectMimeType);
    }

    [Theory]
    [InlineData("matroska,webm", "hevc", 10, "eac3")]
    [InlineData("mkv", "h264", 8, "aac")]
    [InlineData("mp4", "h264", 8, "ac3")]
    [InlineData("mp4", "h264", 8, "dts")]
    [InlineData("mp4", "hevc", 10, "aac")]
    [InlineData("mp4", "h264", 10, "aac")]
    public void Browser_incompatible_sources_need_transcode(string container, string video, int bitDepth, string audio)
    {
        var source = Mp4() with { Container = container, VideoCodec = video, VideoBitDepth = bitDepth, AudioCodec = audio };

        var plan = PlaybackPlanner.Plan(Id, 0, source, Options);

        Assert.Null(plan.DirectPath);
        Assert.Null(plan.DirectMimeType);
    }

    [Fact]
    public void Hdr_interlaced_iso_and_huge_bitrate_are_never_direct()
    {
        Assert.Null(PlaybackPlanner.Plan(Id, 0, Mp4() with { IsHdr = true }, Options).DirectPath);
        Assert.Null(PlaybackPlanner.Plan(Id, 0, Mp4() with { IsInterlaced = true }, Options).DirectPath);
        Assert.Null(PlaybackPlanner.Plan(Id, 0, Mp4() with { IsPlainFile = false }, Options).DirectPath);
        Assert.Null(PlaybackPlanner.Plan(Id, 0, Mp4() with { Bitrate = 60_000_000 }, Options).DirectPath);
        Assert.Null(PlaybackPlanner.Plan(Id, 0, Mp4(), Options with { AllowDirectPlay = false }).DirectPath);
    }

    [Fact]
    public void Silent_video_is_direct_playable()
    {
        var plan = PlaybackPlanner.Plan(Id, 0, Mp4() with { AudioCodec = null }, Options);
        Assert.Equal("video/mp4; codecs=\"avc1.640028\"", plan.DirectMimeType);
    }

    [Fact]
    public void Transcode_url_starts_at_clip_and_downscales()
    {
        var plan = PlaybackPlanner.Plan(Id, 36_000_000_000, Mp4() with { Container = "mkv", AudioCodec = "eac3" }, Options);

        Assert.StartsWith("/Videos/7e440cc9b5f0a60273b9b83091939842/stream.mp4?", plan.TranscodePath, StringComparison.Ordinal);
        Assert.Contains("startTimeTicks=36000000000", plan.TranscodePath, StringComparison.Ordinal);
        Assert.Contains("maxWidth=854", plan.TranscodePath, StringComparison.Ordinal);
        Assert.Contains("videoCodec=h264", plan.TranscodePath, StringComparison.Ordinal);
        Assert.Contains("audioCodec=aac", plan.TranscodePath, StringComparison.Ordinal);
        // 1080p source must be scaled down, not copied.
        Assert.Contains("allowVideoStreamCopy=false", plan.TranscodePath, StringComparison.Ordinal);
        Assert.Contains("allowAudioStreamCopy=false", plan.TranscodePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Small_h264_may_be_stream_copied()
    {
        var plan = PlaybackPlanner.Plan(Id, 0, Mp4() with { Container = "mkv", Width = 720 }, Options);
        Assert.Contains("allowVideoStreamCopy=true", plan.TranscodePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Urls_carry_no_credentials()
    {
        var plan = PlaybackPlanner.Plan(Id, 0, Mp4(), Options);
        Assert.DoesNotContain("api_key", plan.TranscodePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", plan.DirectPath!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_bitrate_is_never_direct()
    {
        Assert.Null(PlaybackPlanner.Plan(Id, 0, Mp4() with { Bitrate = null }, Options).DirectPath);
    }

    [Fact]
    public void User_without_transcode_permission_gets_no_transcode_url()
    {
        var options = new PlaybackPolicy(true, false, null).Apply(Options);

        var direct = PlaybackPlanner.Plan(Id, 0, Mp4(), options);
        var hevc = PlaybackPlanner.Plan(Id, 0, Mp4() with { VideoCodec = "hevc", VideoBitDepth = 10 }, options);

        Assert.NotNull(direct.DirectPath);
        Assert.Null(direct.TranscodePath);
        Assert.Null(hevc.DirectPath);
        Assert.Null(hevc.TranscodePath);
    }

    [Fact]
    public void Remote_bitrate_limit_caps_direct_play_and_transcode()
    {
        var options = new PlaybackPolicy(true, true, 4_000_000).Apply(Options);

        var plan = PlaybackPlanner.Plan(Id, 0, Mp4() with { Bitrate = 8_000_000 }, options);

        Assert.Null(plan.DirectPath);
        Assert.Contains("videoBitRate=1500000", plan.TranscodePath!, StringComparison.Ordinal);

        var tight = new PlaybackPolicy(true, true, 1_000_000).Apply(Options);
        Assert.Equal(872_000, tight.VideoBitrate);
        Assert.Equal(1_000_000, tight.MaxDirectBitrate);
    }

    [Fact]
    public void Unrestricted_policy_changes_nothing()
    {
        Assert.Equal(Options, PlaybackPolicy.Unrestricted.Apply(Options));
    }

    [Fact]
    public void Media_source_id_is_escaped()
    {
        var plan = PlaybackPlanner.Plan(Id, 0, Mp4() with { MediaSourceId = "a&b=c" }, Options);
        Assert.Contains("mediaSourceId=a%26b%3Dc", plan.TranscodePath, StringComparison.Ordinal);
    }
}

public class InjectionTests
{
    private const string Page = "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body><div></div></body></html>";

    [Fact]
    public void Adds_stylesheet_to_head_and_script_to_body()
    {
        var html = IndexHtmlInjector.Inject(Page, string.Empty, "1.0.0.0");

        Assert.Contains("<link rel=\"stylesheet\" data-hover-preview href=\"/HoverPreview/client.css?v=1.0.0.0\" /></head>", html, StringComparison.Ordinal);
        Assert.Contains("<script data-hover-preview defer src=\"/HoverPreview/client.js?v=1.0.0.0\"></script></body>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Injection_is_idempotent()
    {
        var once = IndexHtmlInjector.Inject(Page, string.Empty, "1");
        Assert.Equal(once, IndexHtmlInjector.Inject(once, string.Empty, "1"));
    }

    [Theory]
    // jellyfin-web 10.11's details page template, which File Transformation's "index.html" regex also matches.
    [InlineData("\"use strict\";(self.webpackChunk=self.webpackChunk||[]).push([[76331],{90934:function(e,t,i){i.r(t),t.default='<div id=\"itemDetailPage\"></div>'}}]);")]
    [InlineData("<div class=\"page\"><head></head><body></body></div>")]
    [InlineData("<!DOCTYPE html><html><head></head><p>no body end</p></html>")]
    [InlineData("plain text")]
    public void Non_documents_are_left_untouched(string content)
    {
        Assert.Equal(content, IndexHtmlInjector.Inject(content, string.Empty, "1"));
    }

    [Fact]
    public void Leading_whitespace_and_lowercase_doctype_are_accepted()
    {
        var html = IndexHtmlInjector.Inject("\n  <!doctype html><html><head></head><body></body></html>", string.Empty, "1");
        Assert.Contains(IndexHtmlInjector.Marker, html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("/", "")]
    [InlineData("jellyfin", "/jellyfin")]
    [InlineData("/jellyfin/", "/jellyfin")]
    [InlineData("/media/jf", "/media/jf")]
    [InlineData("\"><script>alert(1)</script>", "")]
    [InlineData("/a b", "")]
    public void Base_url_is_normalized_and_sanitized(string? input, string expected)
    {
        Assert.Equal(expected, IndexHtmlInjector.NormalizeBaseUrl(input));
    }

    [Fact]
    public void Base_url_is_used_in_tags()
    {
        var html = IndexHtmlInjector.Inject(Page, "/jellyfin", "1");
        Assert.Contains("src=\"/jellyfin/HoverPreview/client.js?v=1\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Cache_token_is_encoded()
    {
        var html = IndexHtmlInjector.Inject(Page, string.Empty, "1\"><x");
        Assert.DoesNotContain("1\"><x", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/web/", true)]
    [InlineData("/web/index.html", true)]
    [InlineData("/jellyfin/web/index.html", true)]
    [InlineData("/web/main.js", false)]
    [InlineData("/Items", false)]
    [InlineData("/web", false)]
    [InlineData("", false)]
    public void Fallback_matches_only_index(string path, bool expected)
    {
        Assert.Equal(expected, FallbackInjectionMiddleware.IsIndexPath(path));
    }
}

public class ConfigurationTests
{
    [Fact]
    public void Defaults_map_to_picker_options()
    {
        var options = new PluginConfiguration().ToClipOptions();

        Assert.Equal(TimeSpan.FromSeconds(60), options.Duration);
        Assert.Equal(0.15, options.MovieRangeStart, 3);
        Assert.Equal(0.55, options.MovieRangeEnd, 3);
        Assert.Equal(0.10, options.EpisodeRangeStart, 3);
        Assert.Equal(0.65, options.EpisodeRangeEnd, 3);
    }

    [Fact]
    public void Nonsense_values_are_clamped()
    {
        var config = new PluginConfiguration
        {
            ClipDurationSeconds = 100000,
            HoverDelayMs = -5,
            MovieRangeStartPercent = 90,
            MovieRangeEndPercent = 10,
            TranscodeMaxWidth = 99999,
            TranscodeBitrateKbps = 1,
        };

        var clip = config.ToClipOptions();
        var stream = config.ToStreamOptions();

        Assert.Equal(TimeSpan.FromSeconds(180), clip.Duration);
        Assert.Equal(100, config.ClampedDelayMs);
        Assert.Equal(0.15, clip.MovieRangeStart, 3); // inverted range falls back to defaults
        Assert.Equal(0.55, clip.MovieRangeEnd, 3);
        Assert.Equal(1920, stream.MaxWidth);
        Assert.Equal(300_000, stream.VideoBitrate);
    }

    [Fact]
    public void Fingerprint_changes_with_plan_settings()
    {
        var a = new PluginConfiguration();
        var b = new PluginConfiguration { MovieRangeEndPercent = 40 };
        Assert.NotEqual(a.PlanFingerprint(), b.PlanFingerprint());
    }

    [Fact]
    public void Banner_size_uses_the_banner_transcode_settings()
    {
        var config = new PluginConfiguration();
        var card = config.ToStreamOptions(PreviewSize.Card);
        var banner = config.ToStreamOptions(PreviewSize.Banner);

        Assert.Equal(854, card.MaxWidth);
        Assert.Equal(1_500_000, card.VideoBitrate);
        Assert.Equal(1920, banner.MaxWidth);
        Assert.Equal(6_000_000, banner.VideoBitrate);
        Assert.Equal(card.MaxDirectBitrate, banner.MaxDirectBitrate);

        var clamped = new PluginConfiguration { BannerMaxWidth = 99999, BannerBitrateKbps = 1 }.ToStreamOptions(PreviewSize.Banner);
        Assert.Equal(3840, clamped.MaxWidth);
        Assert.Equal(1_000_000, clamped.VideoBitrate);
    }

    [Fact]
    public void Banner_transcode_of_a_1080p_h264_source_copies_the_video()
    {
        var source = new SourceTraits
        {
            MediaSourceId = "abc",
            Container = "mkv",
            VideoCodec = "h264",
            VideoBitDepth = 8,
            Width = 1920,
            Height = 1080,
            AudioCodec = "ac3",
            Bitrate = 8_000_000,
        };
        var config = new PluginConfiguration();

        var card = PlaybackPlanner.Plan(Guid.NewGuid(), 600_000_000, source, config.ToStreamOptions(PreviewSize.Card));
        var banner = PlaybackPlanner.Plan(Guid.NewGuid(), 600_000_000, source, config.ToStreamOptions(PreviewSize.Banner));

        // Same clip start; the card shrinks the video, the banner can remux it untouched
        Assert.Contains("startTimeTicks=600000000", card.TranscodePath);
        Assert.Contains("startTimeTicks=600000000", banner.TranscodePath);
        Assert.Contains("maxWidth=854", card.TranscodePath);
        Assert.Contains("allowVideoStreamCopy=false", card.TranscodePath);
        Assert.Contains("maxWidth=1920", banner.TranscodePath);
        Assert.Contains("videoBitRate=6000000", banner.TranscodePath);
        Assert.Contains("allowVideoStreamCopy=true", banner.TranscodePath);
    }

    [Fact]
    public void Fingerprint_changes_with_banner_settings()
    {
        var a = new PluginConfiguration();
        var b = new PluginConfiguration { BannerBitrateKbps = 4000 };
        Assert.NotEqual(a.PlanFingerprint(), b.PlanFingerprint());
    }
}
