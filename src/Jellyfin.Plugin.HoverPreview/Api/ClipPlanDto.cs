using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.HoverPreview.Api;

/// <summary>What the hover card should play for one item.</summary>
public sealed class ClipPlanDto
{
    /// <summary>Gets the item that was asked about.</summary>
    [JsonPropertyName("itemId")]
    public Guid ItemId { get; init; }

    /// <summary>Gets the item the clips belong to: the movie itself, or the series for any series/season/episode.</summary>
    [JsonPropertyName("poolId")]
    public Guid PoolId { get; init; }

    /// <summary>Gets "movie", "series", or "none" (still-only).</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "none";

    [JsonPropertyName("heuristicVersion")]
    public int HeuristicVersion { get; init; }

    /// <summary>Gets the clips. Movies get one; shows get a pool the client picks from at random once per page load.</summary>
    [JsonPropertyName("clips")]
    public IReadOnlyList<ClipDto> Clips { get; init; } = Array.Empty<ClipDto>();
}

/// <summary>One playable clip.</summary>
public sealed class ClipDto
{
    /// <summary>Gets the video the clip is cut from (an episode for shows).</summary>
    [JsonPropertyName("itemId")]
    public Guid ItemId { get; init; }

    [JsonPropertyName("mediaSourceId")]
    public string MediaSourceId { get; init; } = string.Empty;

    [JsonPropertyName("startTicks")]
    public long StartTicks { get; init; }

    [JsonPropertyName("durationTicks")]
    public long DurationTicks { get; init; }

    [JsonPropertyName("strategy")]
    public string Strategy { get; init; } = string.Empty;

    /// <summary>Gets the server-relative direct stream URL, or null if the source must be transcoded.</summary>
    [JsonPropertyName("directUrl")]
    public string? DirectUrl { get; init; }

    /// <summary>Gets the MIME type to test with <c>canPlayType</c> before trying <see cref="DirectUrl"/>.</summary>
    [JsonPropertyName("directMimeType")]
    public string? DirectMimeType { get; init; }

    /// <summary>Gets the server-relative transcode URL (low-res, starts at the clip), or null if the user may not transcode.</summary>
    [JsonPropertyName("transcodeUrl")]
    public string? TranscodeUrl { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }
}
