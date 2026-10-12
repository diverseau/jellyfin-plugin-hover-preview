using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.HoverPreview.Api;
using Jellyfin.Plugin.HoverPreview.Clips;
using Jellyfin.Plugin.HoverPreview.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.HoverPreview.Services;

/// <summary>
/// Turns a library item into a <see cref="ClipPlanDto"/>: gathers runtime, chapters, segments and
/// media source details from Jellyfin, runs the pure <see cref="ClipPicker"/>, and caches the result.
/// </summary>
public sealed class ClipPlanService
{
    private const int MaxCacheEntries = 5000;

    // Short enough that new episodes, segment markers (e.g. Intro Skipper) or permission changes show
    // up soon; plans are cheap to rebuild.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(30);

    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IChapterManager _chapterManager;
    private readonly IMediaSegmentManager _segmentManager;
    private readonly ILogger<ClipPlanService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime Created, ClipPlanDto Plan)> _cache = new();

    /// <summary>Initializes a new instance of the <see cref="ClipPlanService"/> class.</summary>
    public ClipPlanService(
        ILibraryManager libraryManager,
        IServerConfigurationManager serverConfigurationManager,
        IMediaSourceManager mediaSourceManager,
        IChapterManager chapterManager,
        IMediaSegmentManager segmentManager,
        ILogger<ClipPlanService> logger)
    {
        _libraryManager = libraryManager;
        _serverConfigurationManager = serverConfigurationManager;
        _mediaSourceManager = mediaSourceManager;
        _chapterManager = chapterManager;
        _segmentManager = segmentManager;
        _logger = logger;
    }

    /// <summary>Builds (or returns the cached) plan for an item.</summary>
    /// <param name="itemId">Any card's item id.</param>
    /// <param name="user">The requesting user, or null for an admin API key.</param>
    /// <param name="isLocalClient">Whether the request comes from the local network (remote bitrate limits apply otherwise).</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan, or null when the item doesn't exist or the user can't see it (indistinguishable on purpose).</returns>
    public async Task<ClipPlanDto?> GetPlanAsync(Guid itemId, User? user, bool isLocalClient, PluginConfiguration config, CancellationToken cancellationToken)
    {
        var item = FindVisible(itemId, user);
        if (item is null)
        {
            return null;
        }

        var series = item switch
        {
            Series s => s,
            Season season => season.Series,
            Episode episode => episode.Series,
            _ => null,
        };

        // The pool is built from the series' episodes, so the series itself must be visible too.
        if (series is not null && user is not null && !IsVisibleTo(series, user))
        {
            return null;
        }

        // Shows share one pool for every card; movies get their own clip.
        BaseItem poolOwner = series ?? item;
        var policy = GetPolicy(user, isLocalClient);
        var cacheKey = string.Join(
            '|',
            poolOwner.Id.ToString("N"),
            poolOwner.DateModified.Ticks,
            user?.Id.ToString("N") ?? "-",
            policy,
            ClipPicker.HeuristicVersion,
            config.PlanFingerprint());

        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow - cached.Created < CacheLifetime)
        {
            return WithRequestedId(cached.Plan, itemId);
        }

        var none = new ClipPlanDto { PoolId = poolOwner.Id, Kind = "none", HeuristicVersion = ClipPicker.HeuristicVersion };
        ClipPlanDto plan;
        try
        {
            var streamOptions = policy.Apply(config.ToStreamOptions());
            plan = !policy.AllowPlayback
                ? none
                : series is not null
                    ? await BuildSeriesPlanAsync(series, user, config, streamOptions, cancellationToken).ConfigureAwait(false)
                    : await BuildMoviePlanAsync(item, user, config, streamOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A broken item must never break the hover card; fall back to still-only.
            _logger.LogWarning(ex, "Hover Preview could not build a clip plan for {ItemId}", poolOwner.Id);
            plan = none;
        }

        Store(cacheKey, plan);
        return WithRequestedId(plan, itemId);
    }

    /// <summary>
    /// Library access (enabled folders) and parental controls. <see cref="BaseItem.IsVisible(User, bool)"/>
    /// alone only checks parental ratings/tags, so the standalone check is required as well.
    /// </summary>
    private static bool IsVisibleTo(BaseItem item, User user) => item.IsVisibleStandalone(user);

    private BaseItem? FindVisible(Guid itemId, User? user)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        return user is null || IsVisibleTo(item, user) ? item : null;
    }

    private void Store(string key, ClipPlanDto plan)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in _cache)
            {
                if (now - entry.Value.Created >= CacheLifetime)
                {
                    _cache.TryRemove(entry.Key, out _);
                }
            }

            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }
        }

        _cache[key] = (DateTime.UtcNow, plan);
    }

    /// <summary>The user's playback permissions and bitrate limit, as Jellyfin's own PlaybackInfo would apply them.</summary>
    private PlaybackPolicy GetPolicy(User? user, bool isLocalClient)
    {
        if (user is null)
        {
            return PlaybackPolicy.Unrestricted;
        }

        int? limit = null;
        if (!isLocalClient)
        {
            var userLimit = user.RemoteClientBitrateLimit ?? 0;
            var serverLimit = _serverConfigurationManager.Configuration.RemoteClientBitrateLimit;
            var effective = userLimit > 0 ? userLimit : serverLimit;
            limit = effective > 0 ? effective : null;
        }

        return new PlaybackPolicy(
            user.HasPermission(PermissionKind.EnableMediaPlayback),
            user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding),
            limit);
    }

    private static ClipPlanDto WithRequestedId(ClipPlanDto plan, Guid itemId) => new()
    {
        ItemId = itemId,
        PoolId = plan.PoolId,
        Kind = plan.Kind,
        HeuristicVersion = plan.HeuristicVersion,
        Clips = plan.Clips,
    };

    private static bool IsPreviewable(BaseItem item) => item is Video video
        && !video.IsPlaceHolder
        && !video.IsVirtualItem
        && video.Video3DFormat is null
        && video.VideoType == VideoType.VideoFile
        && (video.RunTimeTicks ?? 0) > 0;

    private async Task<ClipPlanDto> BuildMoviePlanAsync(BaseItem item, User? user, PluginConfiguration config, StreamOptions streamOptions, CancellationToken cancellationToken)
    {
        var none = new ClipPlanDto { PoolId = item.Id, Kind = "none", HeuristicVersion = ClipPicker.HeuristicVersion };
        if (item is BoxSet || !IsPreviewable(item))
        {
            return none;
        }

        var picker = new ClipPicker(config.ToClipOptions());
        var source = await ToClipSourceAsync(item, cancellationToken).ConfigureAwait(false);
        var window = picker.PickMovie(source);
        var clip = window is null ? null : ToClip(item, window, user, streamOptions);
        return clip is null
            ? none
            : new ClipPlanDto { PoolId = item.Id, Kind = "movie", HeuristicVersion = ClipPicker.HeuristicVersion, Clips = new[] { clip } };
    }

    private async Task<ClipPlanDto> BuildSeriesPlanAsync(Series series, User? user, PluginConfiguration config, StreamOptions streamOptions, CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery(user)
        {
            AncestorIds = new[] { series.Id },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = false,
            Recursive = true,
        };

        // Earliest regular episodes first; specials (season 0) only if there is nothing else.
        var episodes = _libraryManager.GetItemList(query)
            .OfType<Episode>()
            .Where(IsPreviewable)
            .OrderBy(e => (e.ParentIndexNumber ?? 1) <= 0 ? 1 : 0)
            .ThenBy(e => e.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .ThenBy(e => e.PremiereDate ?? DateTime.MaxValue)
            .Take(2)
            .ToList();

        var sources = new List<ClipSource>();
        foreach (var episode in episodes)
        {
            sources.Add(await ToClipSourceAsync(episode, cancellationToken).ConfigureAwait(false));
        }

        var picker = new ClipPicker(config.ToClipOptions());
        var clips = picker.PickSeriesPool(sources)
            .Select(w => ToClip(episodes.First(e => e.Id == w.ItemId), w, user, streamOptions))
            .OfType<ClipDto>()
            .ToList();

        return new ClipPlanDto
        {
            PoolId = series.Id,
            Kind = clips.Count > 0 ? "series" : "none",
            HeuristicVersion = ClipPicker.HeuristicVersion,
            Clips = clips,
        };
    }

    private async Task<ClipSource> ToClipSourceAsync(BaseItem video, CancellationToken cancellationToken)
    {
        var chapters = _chapterManager.GetChapters(video.Id)
            .Select(c => c.StartPositionTicks)
            .ToList();

        var segments = new List<Segment>();
        if (_segmentManager.HasSegments(video.Id))
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(video);
            var found = await _segmentManager.GetSegmentsAsync(video, null, libraryOptions, false).ConfigureAwait(false);
            segments.AddRange(found.Select(s => new Segment(Map(s.Type), s.StartTicks, s.EndTicks)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ClipSource(video.Id, video.RunTimeTicks ?? 0, chapters, segments);
    }

    private ClipDto? ToClip(BaseItem video, ClipWindow window, User? user, StreamOptions streamOptions)
    {
        // Default version only; multi-version items play their first (default) source.
        var mediaSource = _mediaSourceManager.GetStaticMediaSources(video, false, user).FirstOrDefault();
        if (mediaSource is null)
        {
            return null;
        }

        var streams = mediaSource.MediaStreams ?? new List<MediaStream>();
        var videoStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var audioStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.Index == mediaSource.DefaultAudioStreamIndex)
            ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);
        if (videoStream is null)
        {
            return null;
        }

        var traits = new SourceTraits
        {
            MediaSourceId = mediaSource.Id,
            Container = mediaSource.Container,
            VideoCodec = videoStream.Codec,
            VideoBitDepth = videoStream.BitDepth,
            Width = videoStream.Width,
            Height = videoStream.Height,
            IsHdr = videoStream.VideoRange == VideoRange.HDR,
            IsInterlaced = videoStream.IsInterlaced,
            AudioCodec = audioStream?.Codec,
            Bitrate = mediaSource.Bitrate,
            IsPlainFile = mediaSource.Protocol == MediaProtocol.File
                && (mediaSource.VideoType is null or VideoType.VideoFile)
                && mediaSource.IsoType is null,
        };

        var plan = PlaybackPlanner.Plan(video.Id, window.StartTicks, traits, streamOptions);
        if (plan.DirectPath is null && plan.TranscodePath is null)
        {
            return null;
        }

        return new ClipDto
        {
            ItemId = video.Id,
            MediaSourceId = mediaSource.Id,
            StartTicks = window.StartTicks,
            DurationTicks = window.DurationTicks,
            Strategy = window.Strategy,
            DirectUrl = plan.DirectPath,
            DirectMimeType = plan.DirectMimeType,
            TranscodeUrl = plan.TranscodePath,
            Width = videoStream.Width,
            Height = videoStream.Height,
        };
    }

    private static SegmentKind Map(MediaSegmentType type) => type switch
    {
        MediaSegmentType.Intro => SegmentKind.Intro,
        MediaSegmentType.Recap => SegmentKind.Recap,
        MediaSegmentType.Outro => SegmentKind.Outro,
        MediaSegmentType.Commercial => SegmentKind.Commercial,
        MediaSegmentType.Preview => SegmentKind.Preview,
        _ => SegmentKind.Unknown,
    };
}
