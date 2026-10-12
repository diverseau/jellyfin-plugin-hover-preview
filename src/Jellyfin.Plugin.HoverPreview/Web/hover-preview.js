/*
 * Hover Preview for jellyfin-web.
 *
 * One delegated pointer listener watches every `.card[data-id]` on every page (home rows, library
 * grids, search, "More Like This", and cards other plugins inject later). Resting on a card for the
 * configured delay pops out a larger card with the still, logo, controls and metadata, then plays a
 * muted clip chosen by the server. Exactly one popup and one <video> element exist at any time.
 */
(function () {
    'use strict';

    var settings = window.HoverPreviewSettings || {};
    if (!settings.enabled || window.__hoverPreviewLoaded) {
        return;
    }
    window.__hoverPreviewLoaded = true;

    var TICKS_PER_SECOND = 10000000;
    var CARD_SELECTOR = '.card[data-id]';
    var IMAGE_SELECTOR = '.cardImageContainer, .cardImage, .cardScalable';
    var SUPPORTED_TYPES = { Movie: 1, Series: 1, Season: 1, Episode: 1, Video: 1, MusicVideo: 1, BoxSet: 1, Trailer: 1 };
    var OPEN_DELAY = Math.max(100, Number(settings.delayMs) || 500);
    var LEAVE_GRACE = 120;        // pointer may briefly cross a gap without closing
    var PREFETCH_DELAY = 150;     // dwell before data is fetched (skips cards swept across)
    var STILL_MS = 500;           // still shows this long before the clip starts
    var DIRECT_TIMEOUT = 8000;    // no first frame within this -> try the transcode
    var TRANSCODE_TIMEOUT = 20000;
    var MUTE_KEY = 'hoverPreview.muted';

    var hoverMedia = window.matchMedia ? window.matchMedia('(hover: hover) and (pointer: fine)') : null;

    // Per page load: which clip of a show's pool to play. A refresh picks again at random.
    var poolChoice = Object.create(null);
    var planCache = Object.create(null);
    var itemCache = Object.create(null);

    var state = {
        card: null,          // card the popup belongs to (or is pending for)
        pendingTimer: 0,
        prefetchTimer: 0,
        closeTimer: 0,
        stillTimer: 0,
        stallTimer: 0,
        open: false,
        token: 0,            // bumps on every open/close; async work checks it to avoid stale updates
        stream: null         // { url, transcode, playSessionId, startSeconds, durationSeconds, triedTranscode }
    };

    var ui = null;

    // ---------------------------------------------------------------- helpers

    function api() {
        return window.ApiClient || null;
    }

    function el(tag, className, attrs) {
        var node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (attrs) {
            Object.keys(attrs).forEach(function (key) {
                node.setAttribute(key, attrs[key]);
            });
        }
        return node;
    }

    function icon(name) {
        return el('span', 'material-icons ' + name, { 'aria-hidden': 'true' });
    }

    function readMuted() {
        try {
            var stored = window.localStorage.getItem(MUTE_KEY);
            if (stored === 'true' || stored === 'false') {
                return stored === 'true';
            }
        } catch (e) {
            // storage blocked: fall back to the server default
        }
        return settings.startMuted !== false;
    }

    function writeMuted(value) {
        try {
            window.localStorage.setItem(MUTE_KEY, String(value));
        } catch (e) {
            // ignore
        }
    }

    function randomHex(bytes) {
        var data = new Uint8Array(bytes);
        window.crypto.getRandomValues(data);
        return Array.prototype.map.call(data, function (b) {
            return ('0' + b.toString(16)).slice(-2);
        }).join('');
    }

    function formatRuntime(ticks) {
        var minutes = Math.round((ticks || 0) / TICKS_PER_SECOND / 60);
        if (minutes <= 0) {
            return '';
        }
        var h = Math.floor(minutes / 60);
        var m = minutes % 60;
        return h > 0 ? h + 'h ' + m + 'm' : m + 'm';
    }

    function episodeLabel(item) {
        if (item.Type !== 'Episode') {
            return '';
        }
        var label = '';
        if (item.ParentIndexNumber != null && item.IndexNumber != null) {
            label = 'S' + item.ParentIndexNumber + ':E' + item.IndexNumber + ' ';
        }
        return label + '"' + (item.Name || '') + '"';
    }

    function areaEnabled() {
        var areas = settings.areas || {};
        var hash = (window.location.hash || '').toLowerCase();
        if (hash === '' || hash === '#/' || hash.indexOf('#/home') === 0) {
            return areas.home !== false;
        }
        if (hash.indexOf('#/search') === 0) {
            return areas.search !== false;
        }
        if (hash.indexOf('#/details') === 0) {
            return areas.details !== false;
        }
        return areas.libraries !== false;
    }

    function environmentAllows() {
        if (hoverMedia && !hoverMedia.matches) {
            return false;
        }
        var root = document.documentElement.classList;
        if (root.contains('layout-tv') || root.contains('layout-mobile')) {
            return false;
        }
        if (document.hidden || !api()) {
            return false;
        }
        // Never compete with the real player.
        if ((window.location.hash || '').indexOf('#/video') === 0 || document.querySelector('.videoPlayerContainer:not(.hide)')) {
            return false;
        }
        return areaEnabled();
    }

    function cardInfo(card) {
        var type = card.getAttribute('data-type');
        var id = card.getAttribute('data-id');
        if (!id || !type || !SUPPORTED_TYPES[type]) {
            return null;
        }
        return { id: id, type: type, serverId: card.getAttribute('data-serverid') || (api() && api().serverId()) };
    }

    function imageRect(card) {
        var image = card.querySelector(IMAGE_SELECTOR) || card;
        return image.getBoundingClientRect();
    }

    // ---------------------------------------------------------------- data

    /** Caches are per user: switching profiles without a reload must not reuse another user's data. */
    function cacheKey(id) {
        return api().getCurrentUserId() + ':' + id;
    }

    function getItem(id) {
        var key = cacheKey(id);
        var cached = itemCache[key];
        if (cached && Date.now() - cached.at < 60000) {
            return cached.promise;
        }
        var client = api();
        var promise = Promise.resolve(client.getItem(client.getCurrentUserId(), id));
        itemCache[key] = { at: Date.now(), promise: promise };
        promise.catch(function () {
            delete itemCache[key];
        });
        return promise;
    }

    function getPlan(id) {
        if (!settings.video) {
            return Promise.resolve(null);
        }
        var key = cacheKey(id);
        if (planCache[key]) {
            return planCache[key];
        }
        var client = api();
        var promise = Promise.resolve(client.ajax({
            type: 'GET',
            url: client.getUrl('HoverPreview/Clips/' + encodeURIComponent(id)),
            dataType: 'json'
        })).catch(function () {
            delete planCache[key];
            return null;
        });
        planCache[key] = promise;
        return promise;
    }

    function getNextUp(seriesId) {
        var client = api();
        return Promise.resolve(client.getNextUpEpisodes({
            SeriesId: seriesId,
            UserId: client.getCurrentUserId(),
            Limit: 1,
            // Only shows the user has started: an untouched show keeps the "not started" layout.
            DisableFirstEpisode: true,
            Fields: 'MediaSources',
            EnableImages: false
        })).then(function (result) {
            return result && result.Items && result.Items[0] || null;
        }).catch(function () {
            return null;
        });
    }

    function pickClip(plan) {
        if (!plan || !plan.clips || plan.clips.length === 0) {
            return null;
        }
        if (plan.kind !== 'series') {
            return plan.clips[0];
        }
        var key = plan.poolId;
        if (poolChoice[key] == null || poolChoice[key] >= plan.clips.length) {
            poolChoice[key] = Math.floor(Math.random() * plan.clips.length);
        }
        return plan.clips[poolChoice[key]];
    }

    function imageUrl(id, type, tag, maxWidth) {
        return api().getScaledImageUrl(id, { type: type, tag: tag, maxWidth: maxWidth, quality: 90 });
    }

    /**
     * 16:9 still. Backdrops come first because they are textless: card art (Thumb) usually has the
     * title baked in, which would double up with the logo drawn on top. The card's own image is the
     * fallback when it is already landscape.
     */
    function stillUrl(item, card, width) {
        var tags = item.ImageTags || {};
        if (item.BackdropImageTags && item.BackdropImageTags.length) {
            return imageUrl(item.Id, 'Backdrop', item.BackdropImageTags[0], width);
        }
        if (item.ParentBackdropItemId && item.ParentBackdropImageTags && item.ParentBackdropImageTags.length) {
            return imageUrl(item.ParentBackdropItemId, 'Backdrop', item.ParentBackdropImageTags[0], width);
        }
        var image = card.querySelector('.cardImageContainer');
        var rect = imageRect(card);
        if (image && rect.width / Math.max(1, rect.height) > 1.3) {
            var match = /url\(["']?([^"')]+)["']?\)/.exec(window.getComputedStyle(image).backgroundImage || '');
            if (match) {
                return match[1];
            }
        }
        if (tags.Thumb) {
            return imageUrl(item.Id, 'Thumb', tags.Thumb, width);
        }
        if (item.ParentThumbItemId && item.ParentThumbImageTag) {
            return imageUrl(item.ParentThumbItemId, 'Thumb', item.ParentThumbImageTag, width);
        }
        return tags.Primary ? imageUrl(item.Id, 'Primary', tags.Primary, width) : '';
    }

    function stillWidth(card) {
        var popupWidth = Math.min(Math.max(imageRect(card).width * 1.5, 300), 640);
        return Math.round(Math.min(1280, Math.max(400, popupWidth * (window.devicePixelRatio || 1))));
    }

    function logoUrl(item) {
        var tags = item.ImageTags || {};
        if (tags.Logo) {
            return imageUrl(item.Id, 'Logo', tags.Logo, 500);
        }
        if (item.ParentLogoItemId && item.ParentLogoImageTag) {
            return imageUrl(item.ParentLogoItemId, 'Logo', item.ParentLogoImageTag, 500);
        }
        return '';
    }

    function videoWidth(item, clip) {
        if (clip && clip.width) {
            return clip.width;
        }
        var sources = item.MediaSources || [];
        var streams = sources[0] && sources[0].MediaStreams || [];
        for (var i = 0; i < streams.length; i++) {
            if (streams[i].Type === 'Video') {
                return streams[i].Width || 0;
            }
        }
        return 0;
    }

    // ---------------------------------------------------------------- DOM

    function buildUi() {
        var root = el('div', 'hp-root', { id: 'hp-root', 'aria-hidden': 'true' });
        var media = el('div', 'hp-media');
        var still = el('img', 'hp-still', { alt: '', decoding: 'async' });
        var video = el('video', 'hp-video', { playsinline: '', preload: 'auto', disablepictureinpicture: '', disableremoteplayback: '' });
        video.muted = true;
        var shade = el('div', 'hp-shade');
        var logo = el('img', 'hp-logo', { alt: '' });
        var title = el('div', 'hp-title');
        var mute = el('button', 'hp-btn hp-mute', { type: 'button' });
        media.append(still, video, shade, logo, title, mute);

        var panel = el('div', 'hp-panel');
        var buttons = el('div', 'hp-buttons');
        var play = el('button', 'hp-btn hp-btn-play', { type: 'button', title: 'Play', 'aria-label': 'Play' });
        play.appendChild(icon('play_arrow'));
        var fav = el('button', 'hp-btn hp-btn-fav', { type: 'button' });
        var like = el('button', 'hp-btn hp-btn-like', { type: 'button' });
        var more = el('button', 'hp-btn hp-btn-more', { type: 'button', title: 'More info', 'aria-label': 'More info' });
        more.appendChild(icon('expand_more'));
        buttons.append(play, fav, like, more);

        var meta = el('div', 'hp-meta');
        var tags = el('div', 'hp-tags');
        panel.append(buttons, meta, tags);
        root.append(media, panel);
        document.body.appendChild(root);

        ui = { root: root, media: media, still: still, video: video, logo: logo, title: title, mute: mute, play: play, fav: fav, like: like, more: more, meta: meta, tags: tags };

        root.addEventListener('pointerenter', cancelClose);
        root.addEventListener('pointerleave', function (e) {
            if (e.pointerType === 'mouse') {
                scheduleClose();
            }
        });
        // Picture: play (like Netflix). Rest of the card: details. Buttons handle themselves.
        media.addEventListener('click', function (e) {
            if (!e.target.closest('.hp-btn')) {
                playItem();
            }
        });
        panel.addEventListener('click', function (e) {
            if (!e.target.closest('.hp-btn')) {
                openDetails();
            }
        });
        mute.addEventListener('click', toggleMute);
        play.addEventListener('click', playItem);
        more.addEventListener('click', openDetails);
        fav.addEventListener('click', toggleFavorite);
        like.addEventListener('click', toggleLike);

        video.addEventListener('playing', onPlaying);
        video.addEventListener('timeupdate', onTimeUpdate);
        video.addEventListener('ended', endClip);
        video.addEventListener('error', onVideoError);
        video.addEventListener('loadedmetadata', onLoadedMetadata);

        // Match the theme's own progress bar colour if the page has one.
        var bar = document.querySelector('.itemProgressBarForeground');
        if (bar) {
            root.style.setProperty('--hp-progress', window.getComputedStyle(bar).backgroundColor);
        }
        return ui;
    }

    function setIconButton(button, iconName, label, active) {
        button.textContent = '';
        button.appendChild(icon(iconName));
        button.title = label;
        button.setAttribute('aria-label', label);
        button.classList.toggle('hp-active', !!active);
    }

    function renderMute() {
        var muted = ui.video.muted;
        setIconButton(ui.mute, muted ? 'volume_off' : 'volume_up', muted ? 'Unmute' : 'Mute', false);
    }

    function renderUserData(item) {
        var data = item.UserData || {};
        setIconButton(ui.fav, data.IsFavorite ? 'check' : 'add', data.IsFavorite ? 'Remove from favourites' : 'Add to favourites', data.IsFavorite);
        setIconButton(ui.like, data.Likes ? 'thumb_up' : 'thumb_up_off_alt', data.Likes ? 'Remove like' : 'I like this', data.Likes === true);
    }

    function text(className, value) {
        var node = el('span', className);
        node.textContent = value;
        return node;
    }

    function renderMeta(item, clip, nextUp) {
        var meta = ui.meta;
        meta.textContent = '';
        meta.className = 'hp-meta';

        // In progress: S1:E1 "Pilot"  ▬▬▬───  50 of 54m   (Netflix layout)
        var progressItem = null;
        if (item.Type === 'Episode' || item.Type === 'Movie' || item.Type === 'Video') {
            progressItem = item;
        } else if ((item.Type === 'Series' || item.Type === 'Season') && nextUp) {
            progressItem = nextUp;
        }

        var data = progressItem && progressItem.UserData || {};
        var position = data.PlaybackPositionTicks || 0;
        var runtime = progressItem && progressItem.RunTimeTicks || 0;
        var started = progressItem && position > 0 && runtime > 0 && !data.Played;
        var seriesInProgress = progressItem && progressItem !== item;

        if (started || seriesInProgress) {
            meta.classList.add('hp-meta-progress');
            var label = episodeLabel(progressItem);
            if (label) {
                meta.appendChild(text('hp-episode', label));
            }
            if (started) {
                var bar = el('span', 'hp-progress');
                var fill = el('span', 'hp-progress-fill');
                fill.style.width = Math.min(100, Math.max(2, (position / runtime) * 100)) + '%';
                bar.appendChild(fill);
                meta.appendChild(bar);
                var watched = Math.round(position / TICKS_PER_SECOND / 60);
                var total = Math.round(runtime / TICKS_PER_SECOND / 60);
                meta.appendChild(text('hp-dim', watched + ' of ' + total + 'm'));
            }
            return;
        }

        // Not started: [MA 15+]  1h 44m  [HD]
        if (item.OfficialRating) {
            meta.appendChild(text('hp-rating', item.OfficialRating));
        }
        var length = '';
        if (item.Type === 'Series') {
            var seasons = item.ChildCount || 0;
            length = seasons > 0 ? seasons + (seasons === 1 ? ' Season' : ' Seasons') : '';
        } else if (item.Type === 'Season') {
            length = item.ChildCount ? item.ChildCount + ' Episodes' : '';
        } else if (item.Type === 'BoxSet') {
            length = item.ChildCount ? item.ChildCount + ' Titles' : '';
        } else {
            length = formatRuntime(item.RunTimeTicks);
        }
        if (length) {
            meta.appendChild(text('hp-dim', length));
        }
        var width = videoWidth(item, clip);
        if (width >= 3200) {
            meta.appendChild(text('hp-badge', '4K'));
        } else if (width >= 1280) {
            meta.appendChild(text('hp-badge', 'HD'));
        }
    }

    function renderTags(item) {
        ui.tags.textContent = '';
        var genres = (item.Genres || []).slice(0, 3);
        genres.forEach(function (genre, index) {
            if (index > 0) {
                ui.tags.appendChild(text('hp-sep', '•'));
            }
            ui.tags.appendChild(text('hp-tag', genre));
        });
    }

    function renderLogo(item) {
        var url = logoUrl(item);
        ui.title.textContent = '';
        ui.logo.removeAttribute('src');
        ui.logo.hidden = true;
        ui.title.hidden = true;
        if (url) {
            ui.logo.onload = function () {
                ui.logo.hidden = false;
            };
            ui.logo.onerror = function () {
                ui.logo.hidden = true;
                ui.title.hidden = false;
            };
            ui.logo.src = url;
            ui.title.textContent = item.SeriesName || item.Name || '';
        } else {
            ui.title.textContent = item.SeriesName || item.Name || '';
            ui.title.hidden = false;
        }
    }

    function position(card) {
        var rect = imageRect(card);
        var margin = 12;
        var viewportW = document.documentElement.clientWidth;
        var viewportH = window.innerHeight;
        var width = Math.min(Math.max(rect.width * 1.5, 300), 640, viewportW - margin * 2);
        var mediaH = width * 9 / 16;
        var cx = rect.left + rect.width / 2;
        var cy = rect.top + rect.height / 2;

        var left = Math.min(Math.max(cx - width / 2, margin), viewportW - width - margin);
        var root = ui.root;
        root.style.width = width + 'px';
        root.style.fontSize = Math.max(12, Math.min(16, width / 26)) + 'px';
        root.style.left = left + 'px';
        root.style.top = '0px';

        // Centre the 16:9 area on the card image, then keep the whole card on screen.
        var height = root.offsetHeight || mediaH + 150;
        var top = cy - mediaH / 2;
        top = Math.min(top, viewportH - height - margin);
        top = Math.max(top, margin);
        root.style.top = top + 'px';

        // Grow out of the card: start at the card's size, anchored at its centre.
        root.style.transformOrigin = (cx - left) + 'px ' + (cy - top) + 'px';
        root.style.setProperty('--hp-start-scale', String(Math.min(1, rect.width / width)));
    }

    // ---------------------------------------------------------------- open / close

    function cancelPending() {
        clearTimeout(state.pendingTimer);
        clearTimeout(state.prefetchTimer);
        state.pendingTimer = 0;
        state.prefetchTimer = 0;
    }

    function cancelClose() {
        clearTimeout(state.closeTimer);
        state.closeTimer = 0;
    }

    function scheduleClose() {
        cancelPending();
        if (!state.open) {
            state.card = null;
            return;
        }
        if (!state.closeTimer) {
            state.closeTimer = setTimeout(close, LEAVE_GRACE);
        }
    }

    function scheduleOpen(card) {
        cancelPending();
        var info = cardInfo(card);
        if (!info || !environmentAllows()) {
            return;
        }
        state.card = card;

        // Warm the caches during the dwell so the card opens fully populated, including the still.
        // Not for cards the pointer merely sweeps across.
        clearTimeout(state.prefetchTimer);
        state.prefetchTimer = setTimeout(function () {
            if (state.card !== card) {
                return;
            }
            getItem(info.id).then(function (item) {
                var url = stillUrl(item, card, stillWidth(card));
                if (url) {
                    new Image().src = url;
                }
            }).catch(function () {});
            getPlan(info.id);
        }, PREFETCH_DELAY);

        state.pendingTimer = setTimeout(function () {
            state.pendingTimer = 0;
            if (state.card === card && card.isConnected && environmentAllows()) {
                open(card, info);
            }
        }, OPEN_DELAY);
    }

    function open(card, info) {
        if (!ui) {
            buildUi();
        }
        stopVideo();
        cancelClose();
        var token = ++state.token;
        var wasOpen = state.open;
        state.open = true;
        state.info = info;
        state.openCard = card;
        state.item = null;

        var root = ui.root;
        root.classList.remove('hp-playing', 'hp-ready');
        if (!wasOpen) {
            root.classList.remove('hp-open');
        }
        ui.still.removeAttribute('src');
        ui.meta.textContent = '';
        ui.tags.textContent = '';
        ui.title.textContent = '';
        ui.logo.hidden = true;
        ui.video.muted = readMuted();
        renderMute();
        renderUserData({});
        position(card);

        // Open on the next frame so the scale-out transition runs from the card's size.
        window.requestAnimationFrame(function () {
            if (token === state.token) {
                root.classList.add('hp-open');
                root.setAttribute('aria-hidden', 'false');
            }
        });

        getItem(info.id).then(function (item) {
            if (token !== state.token) {
                return null;
            }
            state.item = item;
            var still = stillUrl(item, card, stillWidth(card));
            if (still) {
                ui.still.src = still;
            }
            renderLogo(item);
            renderUserData(item);
            renderTags(item);
            renderMeta(item, null, null);
            position(card);
            root.classList.add('hp-ready');

            var needsNextUp = item.Type === 'Series' || item.Type === 'Season';
            var seriesId = item.Type === 'Series' ? item.Id : item.SeriesId;
            return Promise.all([getPlan(info.id), needsNextUp && seriesId ? getNextUp(seriesId) : null]).then(function (results) {
                if (token !== state.token) {
                    return;
                }
                var clip = pickClip(results[0]);
                renderMeta(item, clip, results[1]);
                position(card);
                if (clip) {
                    state.stillTimer = setTimeout(function () {
                        if (token === state.token) {
                            startClip(clip, token);
                        }
                    }, STILL_MS);
                }
            });
        }).catch(function () {
            // Item lookup failed (deleted, no access): show nothing rather than a broken card.
            if (token === state.token) {
                close();
            }
        });
    }

    /** Hides the popup but leaves any pending open (for the next card) alone. */
    function hidePopup() {
        state.closeTimer = 0;
        if (!state.open) {
            return;
        }
        state.token++;
        state.open = false;
        state.item = null;
        state.openCard = null;
        stopVideo();
        if (ui) {
            ui.root.classList.remove('hp-open', 'hp-playing');
            ui.root.setAttribute('aria-hidden', 'true');
        }
    }

    function close() {
        cancelPending();
        cancelClose();
        state.card = null;
        hidePopup();
    }

    // ---------------------------------------------------------------- video

    function streamUrl(path, playSessionId) {
        var client = api();
        var deviceId = 'hoverpreview-' + (typeof client.deviceId === 'function' ? client.deviceId() : 'web');
        var url = client.getUrl(path.replace(/^\//, ''));
        return url + (url.indexOf('?') < 0 ? '?' : '&')
            + 'ApiKey=' + encodeURIComponent(client.accessToken())
            + '&deviceId=' + encodeURIComponent(deviceId)
            + '&playSessionId=' + encodeURIComponent(playSessionId);
    }

    function startClip(clip, token) {
        var video = ui.video;
        var canDirect = !!clip.directUrl && !!clip.directMimeType && video.canPlayType(clip.directMimeType) !== '';
        if (!canDirect && !clip.transcodeUrl) {
            // Not playable here and the user may not transcode: still image only.
            return;
        }
        state.stream = {
            clip: clip,
            token: token,
            transcode: !canDirect,
            playSessionId: randomHex(16),
            startSeconds: clip.startTicks / TICKS_PER_SECOND,
            durationSeconds: clip.durationTicks / TICKS_PER_SECOND,
            seeked: false
        };
        loadStream();
    }

    function loadStream() {
        var stream = state.stream;
        var video = ui.video;
        var path = stream.transcode ? stream.clip.transcodeUrl : stream.clip.directUrl;
        var url = streamUrl(path, stream.playSessionId);

        // A direct file has to seek to the clip; a transcode already starts there.
        video.src = stream.transcode ? url : url + '#t=' + stream.startSeconds.toFixed(2);
        video.load();
        var attempt = video.play();
        if (attempt && attempt.catch) {
            attempt.catch(function (err) {
                if (!err || err.name !== 'NotAllowedError' || state.stream !== stream) {
                    return;
                }
                if (!video.muted) {
                    // Autoplay with sound refused: retry muted.
                    video.muted = true;
                    renderMute();
                    video.play().catch(function () {
                        giveUp(stream);
                    });
                } else {
                    giveUp(stream);
                }
            });
        }
        clearTimeout(state.stallTimer);
        state.stallTimer = setTimeout(onVideoError, stream.transcode ? TRANSCODE_TIMEOUT : DIRECT_TIMEOUT);
    }

    /** The browser refuses to autoplay at all: keep the still, don't fall back to a transcode. */
    function giveUp(stream) {
        if (state.stream === stream) {
            stream.autoplayBlocked = true;
            endClip();
        }
    }

    function clipElapsed() {
        var stream = state.stream;
        return stream.transcode ? ui.video.currentTime : ui.video.currentTime - stream.startSeconds;
    }

    function onLoadedMetadata() {
        var stream = state.stream;
        if (stream && !stream.transcode && !stream.seeked) {
            stream.seeked = true;
            if (Math.abs(ui.video.currentTime - stream.startSeconds) > 1) {
                ui.video.currentTime = stream.startSeconds;
            }
        }
    }

    function onPlaying() {
        if (!state.stream) {
            return;
        }
        clearTimeout(state.stallTimer);
        ui.root.classList.add('hp-playing');
    }

    function onTimeUpdate() {
        var stream = state.stream;
        if (stream && clipElapsed() >= stream.durationSeconds) {
            endClip();
        }
    }

    function onVideoError() {
        var stream = state.stream;
        clearTimeout(state.stallTimer);
        if (!stream) {
            return;
        }
        if (!stream.transcode && stream.clip.transcodeUrl && !stream.autoplayBlocked) {
            // Direct play failed (codec, container, range requests): fall back to a transcode once.
            releaseStream();
            stream.transcode = true;
            stream.playSessionId = randomHex(16);
            state.stream = stream;
            loadStream();
            return;
        }
        endClip();
    }

    /** Clip finished: fade back to the still and stay open, like Netflix. */
    function endClip() {
        var stream = state.stream;
        if (!stream) {
            return;
        }
        clearTimeout(state.stallTimer);
        ui.root.classList.remove('hp-playing');
        // Let the video fade out before tearing it down, unless another clip has started meanwhile.
        setTimeout(function () {
            if (state.stream === stream) {
                stopVideo();
            }
        }, 400);
    }

    function releaseStream() {
        var video = ui.video;
        video.pause();
        video.removeAttribute('src');
        video.load(); // aborts the HTTP request
    }

    function stopEncoding(stream, keepalive) {
        var client = api();
        if (!client || !stream.transcode) {
            return;
        }
        var deviceId = 'hoverpreview-' + (typeof client.deviceId === 'function' ? client.deviceId() : 'web');
        var url = client.getUrl('Videos/ActiveEncodings', { deviceId: deviceId, playSessionId: stream.playSessionId });
        if (keepalive && window.fetch) {
            // The page is going away: a keepalive request survives the unload, an XHR would not.
            window.fetch(url, {
                method: 'DELETE',
                keepalive: true,
                headers: { Authorization: 'MediaBrowser Token="' + client.accessToken() + '"' }
            }).catch(function () {});
            return;
        }
        Promise.resolve(client.ajax({ type: 'DELETE', url: url })).catch(function () {});
    }

    function stopVideo() {
        clearTimeout(state.stillTimer);
        clearTimeout(state.stallTimer);
        var stream = state.stream;
        state.stream = null;
        if (!ui || !stream) {
            return;
        }
        releaseStream();
        // Make sure ffmpeg stops now rather than when the server notices the dropped connection.
        stopEncoding(stream, false);
    }

    // ---------------------------------------------------------------- buttons

    function toggleMute(e) {
        e.stopPropagation();
        var video = ui.video;
        video.muted = !video.muted;
        if (!video.muted) {
            video.volume = 1;
        }
        writeMuted(video.muted);
        renderMute();
    }

    /**
     * Navigates the way jellyfin-web itself does (the card's own link, then its router). Writing the
     * URL hash directly bypasses the router and left some pages half rendered.
     */
    function openDetails() {
        var info = state.info;
        var card = state.openCard;
        if (!info) {
            return;
        }
        close();
        var link = card && card.isConnected && card.querySelector('[data-action="link"]');
        if (link) {
            link.click();
            return;
        }
        var router = window.Emby && window.Emby.Page;
        if (router && typeof router.showItem === 'function') {
            router.showItem(info.id, info.serverId);
            return;
        }
        window.location.hash = '#/details?id=' + encodeURIComponent(info.id) + '&serverId=' + encodeURIComponent(info.serverId || '');
    }

    /**
     * Starts playback exactly as the card's own Play button would: from the beginning if unwatched,
     * from the resume point if started, next-up for shows. Never from the preview position.
     */
    function playItem() {
        // The open card, not a neighbour the pointer may be pending on.
        var card = state.openCard;
        var native = card && card.isConnected && card.querySelector('[data-action="resume"], [data-action="play"]');
        if (native) {
            close();
            native.click();
        } else {
            openDetails();
        }
    }

    function updateUserData(apply) {
        var item = state.item;
        var info = state.info;
        if (!item || !info) {
            return;
        }
        var client = api();
        var token = state.token;
        new Promise(function (resolve) {
            resolve(apply(client, client.getCurrentUserId(), item));
        }).then(function (userData) {
            // The reply is the complete user data. Jellyfin omits null fields (Likes after an
            // unlike), so merging it into the old state would keep the stale value.
            if (userData && typeof userData === 'object') {
                item.UserData = userData;
            }
            delete itemCache[cacheKey(info.id)];
            if (token === state.token) {
                renderUserData(item);
            }
        }).catch(function () {});
    }

    function toggleFavorite(e) {
        e.stopPropagation();
        updateUserData(function (client, userId, item) {
            var isFavorite = !!(item.UserData && item.UserData.IsFavorite);
            return client.updateFavoriteStatus(userId, item.Id, !isFavorite);
        });
    }

    function toggleLike(e) {
        e.stopPropagation();
        updateUserData(function (client, userId, item) {
            var liked = !!(item.UserData && item.UserData.Likes);
            if (!liked) {
                return client.updateUserItemRating(userId, item.Id, true);
            }
            // jellyfin-web 10.11 calls it clearUserItemRating; older apiclients deleteUserItemRating.
            var clear = client.clearUserItemRating || client.deleteUserItemRating;
            return clear.call(client, userId, item.Id);
        });
    }

    // ---------------------------------------------------------------- wiring

    /**
     * Opening needs real pointer movement (pointermove). Chrome also fires pointerover when a page
     * renders under a resting cursor; that may close or keep a card but never opens one, otherwise
     * every navigation would pop out whatever card lands under the mouse.
     */
    function onPointer(e, moved) {
        if (e.pointerType !== 'mouse') {
            return;
        }
        var target = e.target;
        if (!(target instanceof Element)) {
            return;
        }
        if (ui && ui.root.contains(target)) {
            cancelClose();
            // Back on the open card's popup: forget any neighbour that was pending.
            if (state.open && state.card !== state.openCard) {
                cancelPending();
                state.card = state.openCard;
            }
            return;
        }
        var card = target.closest(CARD_SELECTOR);
        if (card && card === state.card) {
            // Only the popup's own card keeps it open; a neighbour waiting to open must not.
            if (card === state.openCard) {
                cancelClose();
            }
            return;
        }
        if (card && moved) {
            // A different card: the open popup goes away and the new one waits the full delay.
            cancelClose();
            scheduleOpen(card);
            if (state.open) {
                state.closeTimer = setTimeout(hidePopup, LEAVE_GRACE);
            }
            return;
        }
        scheduleClose();
    }

    // Chrome also sends pointermove after layout/scroll with an unchanged position; only a changed
    // position counts as the user moving the mouse.
    var lastPointer = { x: -1, y: -1 };
    document.addEventListener('pointermove', function (e) {
        // The very first event after page load only establishes where the pointer rests.
        var moved = lastPointer.x >= 0 && (e.clientX !== lastPointer.x || e.clientY !== lastPointer.y);
        lastPointer.x = e.clientX;
        lastPointer.y = e.clientY;
        onPointer(e, moved);
    }, { passive: true });

    document.addEventListener('pointerover', function (e) {
        onPointer(e, false);
    }, { passive: true });

    document.addEventListener('pointerout', function (e) {
        // Pointer left the window.
        if (e.pointerType === 'mouse' && !e.relatedTarget) {
            scheduleClose();
        }
    }, { passive: true });

    // Anything that moves cards under a fixed popup closes it.
    document.addEventListener('scroll', function (e) {
        if (!ui || !ui.root.contains(e.target)) {
            close();
        }
    }, { capture: true, passive: true });
    window.addEventListener('resize', close);
    window.addEventListener('pagehide', function () {
        var stream = state.stream;
        state.stream = null;
        if (stream) {
            stopEncoding(stream, true);
        }
    });
    window.addEventListener('blur', close);
    window.addEventListener('hashchange', close);
    document.addEventListener('viewbeforehide', close, true);
    document.addEventListener('visibilitychange', function () {
        if (document.hidden) {
            close();
        }
    });
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
            close();
        }
    });
    document.addEventListener('pointerdown', function (e) {
        // Clicking anywhere else (including the card's own buttons) closes the popup.
        if (state.open && !(ui && ui.root.contains(e.target))) {
            close();
        }
    }, true);
})();
