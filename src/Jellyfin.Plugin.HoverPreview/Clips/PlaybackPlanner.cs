using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.HoverPreview.Clips;

/// <summary>The bits of a media source that decide how a preview can be streamed.</summary>
public sealed record SourceTraits
{
    public string MediaSourceId { get; init; } = string.Empty;

    /// <summary>Gets the container; Jellyfin may report a comma separated list ("mov,mp4,m4a").</summary>
    public string? Container { get; init; }

    public string? VideoCodec { get; init; }

    public int? VideoBitDepth { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Gets a value indicating whether the video is HDR (needs tone mapping in a browser).</summary>
    public bool IsHdr { get; init; }

    public bool IsInterlaced { get; init; }

    /// <summary>Gets the default audio codec, or null when there is no audio.</summary>
    public string? AudioCodec { get; init; }

    /// <summary>Gets the overall bitrate in bits per second, if known.</summary>
    public int? Bitrate { get; init; }

    /// <summary>Gets a value indicating whether the source is a plain file (not ISO, BluRay/DVD folder, remote stream).</summary>
    public bool IsPlainFile { get; init; } = true;
}

/// <summary>Streaming knobs the server applies to previews.</summary>
public sealed record StreamOptions
{
    public int MaxWidth { get; init; } = 854;

    public int VideoBitrate { get; init; } = 1_500_000;

    public int AudioBitrate { get; init; } = 128_000;

    /// <summary>Gets the largest source bitrate that may be sent to the browser untouched.</summary>
    public int MaxDirectBitrate { get; init; } = 20_000_000;

    public bool AllowDirectPlay { get; init; } = true;

    /// <summary>Gets a value indicating whether a transcode URL may be offered (the user's transcoding permission).</summary>
    public bool AllowTranscode { get; init; } = true;
}

/// <summary>What the requesting user is allowed to play, mirroring Jellyfin's own playback checks.</summary>
/// <param name="AllowPlayback">The user's "allow media playback" permission.</param>
/// <param name="AllowTranscode">The user's "allow video playback that requires transcoding" permission.</param>
/// <param name="BitrateLimit">Remote client bitrate limit in bits per second, or null when none applies.</param>
public sealed record PlaybackPolicy(bool AllowPlayback, bool AllowTranscode, int? BitrateLimit)
{
    /// <summary>Gets the policy for admin API keys.</summary>
    public static PlaybackPolicy Unrestricted { get; } = new(true, true, null);

    /// <summary>Tightens the admin's stream settings to what this user may use.</summary>
    /// <param name="options">The configured stream options.</param>
    /// <returns>Options no more generous than the policy.</returns>
    public StreamOptions Apply(StreamOptions options)
    {
        var result = options with { AllowTranscode = options.AllowTranscode && AllowTranscode };
        if (BitrateLimit is int limit)
        {
            result = result with
            {
                MaxDirectBitrate = Math.Min(result.MaxDirectBitrate, limit),
                // Leave room for the audio track inside the limit.
                VideoBitrate = Math.Max(100_000, Math.Min(result.VideoBitrate, limit - result.AudioBitrate)),
            };
        }

        return result;
    }
}

/// <summary>How the client should fetch a clip.</summary>
/// <param name="DirectPath">Server-relative URL for direct play, or null when the source needs transcoding.</param>
/// <param name="DirectMimeType">MIME type with codecs for <c>canPlayType</c>, or null.</param>
/// <param name="TranscodePath">Server-relative URL of a low-resolution transcode starting at the clip, or null when transcoding isn't allowed.</param>
public sealed record StreamPlan(string? DirectPath, string? DirectMimeType, string? TranscodePath);

/// <summary>
/// Decides between direct play and a low resolution transcode and builds the stream URLs. The
/// URLs carry no credentials or session ids; the client appends those.
/// </summary>
public static class PlaybackPlanner
{
    private static readonly string[] DirectContainers = { "mp4", "m4v", "mov", "webm" };
    private static readonly string[] BrowserAudio = { "aac", "mp3", "opus", "vorbis", "flac" };

    /// <summary>Builds the stream URLs for one clip.</summary>
    /// <param name="itemId">The video to stream (an episode for shows).</param>
    /// <param name="startTicks">Where the clip starts.</param>
    /// <param name="source">The media source.</param>
    /// <param name="options">Streaming knobs.</param>
    /// <returns>The plan.</returns>
    public static StreamPlan Plan(Guid itemId, long startTicks, SourceTraits source, StreamOptions options)
    {
        var id = itemId.ToString("N", CultureInfo.InvariantCulture);
        var mediaSourceId = Uri.EscapeDataString(source.MediaSourceId);

        string? directPath = null;
        string? mime = null;
        if (options.AllowDirectPlay && TryDirectMime(source, options, out var directMime))
        {
            directPath = $"/Videos/{id}/stream?static=true&mediaSourceId={mediaSourceId}";
            mime = directMime;
        }

        // Low-res h264/aac fragmented MP4: playable progressively by every browser. Copying the video
        // stream is only allowed when it's already small h264, otherwise we'd ship a 4K stream to a
        // 400px card.
        var canCopyVideo = Is(source.VideoCodec, "h264") && (source.VideoBitDepth ?? 8) <= 8 && !source.IsHdr
            && (source.Width ?? int.MaxValue) <= options.MaxWidth;
        var query = new List<string>
        {
            "mediaSourceId=" + mediaSourceId,
            "startTimeTicks=" + startTicks.ToString(CultureInfo.InvariantCulture),
            "videoCodec=h264",
            "audioCodec=aac",
            "maxWidth=" + options.MaxWidth.ToString(CultureInfo.InvariantCulture),
            "videoBitRate=" + options.VideoBitrate.ToString(CultureInfo.InvariantCulture),
            "audioBitRate=" + options.AudioBitrate.ToString(CultureInfo.InvariantCulture),
            "audioChannels=2",
            "allowVideoStreamCopy=" + (canCopyVideo ? "true" : "false"),
            "allowAudioStreamCopy=" + (Is(source.AudioCodec, "aac") ? "true" : "false"),
        };
        var transcodePath = options.AllowTranscode ? $"/Videos/{id}/stream.mp4?" + string.Join('&', query) : null;

        return new StreamPlan(directPath, mime, transcodePath);
    }

    /// <summary>Whether a browser can very likely play the file as-is, and with which MIME type.</summary>
    internal static bool TryDirectMime(SourceTraits s, StreamOptions options, out string mime)
    {
        mime = string.Empty;
        if (!s.IsPlainFile || s.IsHdr || s.IsInterlaced)
        {
            return false;
        }

        // Unknown bitrate can't be checked against the limit, so it is never sent untouched.
        if (s.Bitrate is not int bitrate || bitrate > options.MaxDirectBitrate)
        {
            return false;
        }

        var containers = (s.Container ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var container = DirectContainers.FirstOrDefault(c => containers.Contains(c, StringComparer.OrdinalIgnoreCase));
        if (container is null)
        {
            return false;
        }

        if (s.AudioCodec is not null && !BrowserAudio.Any(a => Is(s.AudioCodec, a)))
        {
            return false;
        }

        string? videoCodecs = null;
        if (Is(s.VideoCodec, "h264") && (s.VideoBitDepth ?? 8) <= 8)
        {
            videoCodecs = "avc1.640028";
        }
        else if (Is(s.VideoCodec, "vp9"))
        {
            videoCodecs = "vp09.00.40.08";
        }
        else if (Is(s.VideoCodec, "av1"))
        {
            videoCodecs = (s.VideoBitDepth ?? 8) > 8 ? "av01.0.08M.10" : "av01.0.08M.08";
        }

        if (videoCodecs is null)
        {
            return false;
        }

        var audioCodecs = s.AudioCodec?.ToLowerInvariant() switch
        {
            null => null,
            "aac" => "mp4a.40.2",
            "mp3" => "mp4a.69",
            "opus" => "opus",
            "vorbis" => "vorbis",
            "flac" => "flac",
            _ => null,
        };

        var type = container == "webm" ? "video/webm" : "video/mp4";
        var codecs = audioCodecs is null ? videoCodecs : videoCodecs + "," + audioCodecs;
        mime = $"{type}; codecs=\"{codecs}\"";
        return true;
    }

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
