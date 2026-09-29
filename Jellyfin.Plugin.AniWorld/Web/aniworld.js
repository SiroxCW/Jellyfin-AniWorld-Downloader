export default function (view, params) {

    // Load the bundled Font Awesome stylesheet (the web client does not ship it).
    // Idempotent: the element is removed on viewhide, so re-adding on re-open is safe.
    if (!view.querySelector('#aw-fontawesome-css')) {
        var faLink = document.createElement('link');
        faLink.id = 'aw-fontawesome-css';
        faLink.rel = 'stylesheet';
        faLink.href = ApiClient.getUrl('AniWorld/fontawesome/css/all.min.css');
        view.appendChild(faLink);
    }

    function esc(str) {
        if (!str) return '';
        var d = document.createElement('div');
        d.textContent = str;
        return d.innerHTML;
    }

    // Escape a string for safe inclusion inside a JS single-quoted string in an HTML attribute.
    // Prevents XSS via crafted provider names or titles breaking out of onclick="...fn('HERE')".
    function escJs(str) {
        if (!str) return '';
        return str.replace(/\\/g, '\\\\').replace(/'/g, "\\'").replace(/"/g, '&quot;');
    }

    function formatSize(bytes) {
        if (!bytes || bytes === 0) return '0 B';
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
        if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
        var gb = bytes / (1024 * 1024 * 1024);
        if (gb < 1024) return gb.toFixed(2) + ' GB';
        return (gb / 1024).toFixed(2) + ' TB';
    }

    function formatCount(n) {
        if (n == null) return '0';
        if (n < 1000) return String(n);
        if (n < 1000000) return (n / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
        return (n / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
    }

    function formatDate(isoStr) {
        if (!isoStr) return '\u2014';
        try {
            var d = new Date(isoStr);
            var now = new Date();
            var diffMs = now - d;
            var diffMins = Math.floor(diffMs / 60000);

            if (diffMins < 1) return 'just now';
            if (diffMins < 60) return diffMins + 'm ago';
            var diffHrs = Math.floor(diffMins / 60);
            if (diffHrs < 24) return diffHrs + 'h ago';
            var diffDays = Math.floor(diffHrs / 24);
            if (diffDays < 7) return diffDays + 'd ago';

            return d.toLocaleDateString();
        } catch (e) {
            return isoStr;
        }
    }

    // Sources that carry a full English DUB (not just subtitles).
    function isDubSource(source) {
        return source === 'sto' || source === 'filmo' || source === 'filmpalast'
            || source === 'megakino' || source === 'moflix';
    }

    // Language names per source (plain text)
    function getLangNames(source) {
        if (isDubSource(source)) {
            return { '1': 'German Dub', '2': 'English Dub' };
        }
        return { '1': 'German Dub', '2': 'English Sub', '3': 'German Sub' };
    }

    // Build language label HTML with flag SVG
    function langLabelHtml(langKey, source) {
        var names = getLangNames(source);
        var name = names[langKey] || ('Language ' + langKey);
        var flagUrl = ApiClient.getUrl('AniWorld/Flag/' + langKey, { source: source || 'aniworld' });
        return '<img src="' + flagUrl + '" style="height:1.1em;vertical-align:middle;margin-right:0.4em" onerror="this.style.display=\'none\'">' + esc(name);
    }

    // Build language options HTML for season bar
    function getLangOptionsHtml(source) {
        var html = '<option value="">\uD83C\uDF10 Use Settings Default</option>';
        html += '<option value="1">\uD83C\uDDE9\uD83C\uDDEA German Dub</option>';
        if (isDubSource(source)) {
            html += '<option value="2">\uD83C\uDDEC\uD83C\uDDE7 English Dub</option>';
        } else {
            html += '<option value="2">\uD83C\uDDEC\uD83C\uDDE7 English Sub</option>';
            html += '<option value="3">\uD83C\uDDE9\uD83C\uDDEA German Sub</option>';
        }
        return html;
    }

    // Get site logo URL
    function siteLogoUrl(source) {
        return ApiClient.getUrl('AniWorld/SiteLogo/' + (source || 'aniworld'));
    }

    // Display names for the providers, used in the selector, placeholders and loading hints
    var PROVIDER_NAMES = {
        aniworld: 'AniWorld',
        sto: 'SerienStream',
        filmo: 'Filmo',
        filmpalast: 'FilmPalast',
        megakino: 'MegaKino',
        moflix: 'Moflix'
    };

    var AW = {
        currentSeriesTitle: null,
        currentSeriesUrl: null,
        currentSeriesSource: null,
        currentSeasonUrl: null,
        lastSearchQuery: null,
        lastSearchResults: null,
        downloadPollInterval: null,
        activeDownloadCount: 0,
        historyOffset: 0,
        historyStatusFilter: null,
        historySeriesFilter: null,
        seasonGeneration: 0,

        aniWorldOnlyGerman: false,

        // ── Browse / provider state ──
        enabledSources: { aniworld: true, sto: false, filmo: false, filmpalast: false, megakino: false, moflix: false },
        currentProvider: 'aniworld',
        viewMode: 'browse',         // 'browse' | 'search' | 'series'
        providerBrowse: {},         // { [source]: { new: BrowseItem[], popular: BrowseItem[] } }
        lastSearchContext: null,    // { query, all } of the most recent search, for goBack

        // ── Tab switching ──
        switchTab: function (tab) {
            view.querySelectorAll('.aw-tab').forEach(function (t) { t.classList.remove('active'); });
            view.querySelector('[data-tab="' + tab + '"]').classList.add('active');
            view.querySelector('#browseTab').style.display = tab === 'browse' ? '' : 'none';
            view.querySelector('#downloadsTab').style.display = tab === 'downloads' ? '' : 'none';
            view.querySelector('#historyTab').style.display = tab === 'history' ? '' : 'none';

            if (tab === 'downloads') {
                this.loadDownloads();
                this.startPolling();
            } else {
                this.stopPolling();
            }

            if (tab === 'history') {
                this.historyOffset = 0;
                this.loadStats();
                this.loadHistory(true);
            }

            if (tab === 'browse') {
                this.showCurrentView();
            }
        },

        // ── Browse / provider ──
        // Section titles per provider, like the main AniWorld-Downloader project
        PROVIDER_SECTIONS: {
            aniworld: { new: 'New Animes', popular: 'Popular Animes' },
            sto: { new: 'New Series', popular: 'Popular Series' },
            filmo: { new: 'New Movies', popular: 'Popular Movies' },
            filmpalast: { new: 'New Movies', popular: 'Popular Movies' },
            megakino: { new: 'New Titles', popular: 'Popular Titles' },
            moflix: { new: 'New Titles', popular: 'Popular Titles' }
        },

        // Render the provider buttons (only the enabled ones) and position the sliding thumb
        buildProviderControl: function () {
            var track = view.querySelector('#aw-provider-track');
            if (!track) return;

            var enabled = ['aniworld', 'sto', 'filmo', 'filmpalast', 'megakino', 'moflix'].filter(function (s) { return AW.enabledSources[s]; });
            if (enabled.length === 0) enabled = ['aniworld'];

            // If the current provider got disabled in the settings, fall back to the first enabled one
            if (enabled.indexOf(AW.currentProvider) === -1) {
                AW.currentProvider = enabled[0];
            }

            // With a single enabled provider a selector would just be noise
            var providerRow = view.querySelector('#aw-provider');
            if (providerRow) providerRow.style.display = enabled.length > 1 ? '' : 'none';

            // Rebuild buttons (the thumb stays)
            track.querySelectorAll('.aw-provider-btn').forEach(function (b) { b.remove(); });
            enabled.forEach(function (s) {
                var btn = document.createElement('button');
                btn.className = 'aw-provider-btn' + (s === AW.currentProvider ? ' active' : '');
                btn.dataset.source = s;
                btn.textContent = PROVIDER_NAMES[s];
                btn.onclick = function () { AW.switchProvider(s); };
                track.appendChild(btn);
            });

            this._setupThumbSync(track);
            this._updateProviderThumb();
            this._updateSearchPlaceholder();
        },

        // Keep the thumb in sync with the active button when the layout changes
        // (window resize, the scrollbar appearing or disappearing, font loading).
        _setupThumbSync: function (track) {
            if (typeof ResizeObserver === 'undefined') return;
            if (!AW._thumbRO) {
                AW._thumbRO = new ResizeObserver(function () { AW._updateProviderThumb(); });
                AW._thumbRO.observe(track);
            }
            // Buttons are rebuilt on every call, so observe the new ones (removed ones are gone)
            track.querySelectorAll('.aw-provider-btn').forEach(function (b) { AW._thumbRO.observe(b); });
        },

        _updateProviderThumb: function () {
            var track = view.querySelector('#aw-provider-track');
            var thumb = view.querySelector('#aw-provider-thumb');
            if (!track || !thumb) return;
            var active = track.querySelector('.aw-provider-btn.active');
            if (!active) {
                thumb.style.width = '0px';
                thumb.style.transform = 'translateX(0px)';
                return;
            }
            // The thumb's base position (left:0) and the button's offsetLeft share the
            // track's padding edge as origin, so offsetLeft can be used as-is.
            thumb.style.width = active.offsetWidth + 'px';
            thumb.style.transform = 'translateX(' + active.offsetLeft + 'px)';
        },

        _updateSearchPlaceholder: function () {
            var input = view.querySelector('#aw-search-input');
            if (input) input.placeholder = 'Search ' + (PROVIDER_NAMES[this.currentProvider] || '') + '...';
        },

        // Switch the selected provider: move thumb + placeholder, then show its browse rows
        switchProvider: function (source) {
            if (source === this.currentProvider) return;
            this.currentProvider = source;
            this.lastSearchContext = null;

            view.querySelectorAll('.aw-provider-btn').forEach(function (b) {
                b.classList.toggle('active', b.dataset.source === source);
            });
            this._updateProviderThumb();
            this._updateSearchPlaceholder();

            this.showProviderBrowse(source);
        },

        // Decide what the Browse tab shows: the series detail, the last search, or the provider rows
        showCurrentView: function () {
            if (this.viewMode === 'series' && this.currentSeriesUrl) {
                this.showSeries(encodeURIComponent(this.currentSeriesUrl), this.currentSeriesTitle, this.currentSeriesSource);
                return;
            }
            if (this.viewMode === 'search' && this.lastSearchContext) {
                this._runSearch(this.lastSearchContext.query, this.lastSearchContext.all);
                return;
            }
            this.showProviderBrowse(this.currentProvider);
        },

        // Show the New + Popular rows of one provider (cached per provider)
        showProviderBrowse: function (source) {
            this.viewMode = 'browse';
            this.lastSearchContext = null;

            var content = view.querySelector('#aw-content');
            if (!content) return;

            if (this.providerBrowse[source]) {
                this._renderProviderBrowse(source, content);
                return;
            }

            content.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading...</div>';

            var self = this;
            var load = function (endpoint) {
                return ApiClient.fetch({
                    url: ApiClient.getUrl(endpoint, { source: source }),
                    type: 'GET', dataType: 'json'
                }).catch(function () { return []; });
            };
            Promise.all([load('AniWorld/New'), load('AniWorld/Popular')]).then(function (results) {
                self.providerBrowse[source] = { new: results[0] || [], popular: results[1] || [] };
                // Only paint if the user is still looking at this provider's rows
                if (self.currentProvider === source && self.viewMode === 'browse') {
                    self._renderProviderBrowse(source, content);
                }
            });
        },

        _renderProviderBrowse: function (source, content) {
            var cache = this.providerBrowse[source] || {};
            var newItems = cache.new || [];
            var popularItems = cache.popular || [];
            var titles = this.PROVIDER_SECTIONS[source] || {};

            if (newItems.length === 0 && popularItems.length === 0) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="fa-solid fa-inbox"></i></div>No content found.</div>';
                return;
            }

            var html = '';
            if (newItems.length > 0) {
                html += '<div class="aw-browse-section-title">' + titles.new + '</div>';
                html += this._buildBrowseGrid(newItems, source);
            }
            if (popularItems.length > 0) {
                html += '<div class="aw-browse-section-title">' + titles.popular + '</div>';
                html += this._buildBrowseGrid(popularItems, source);
            }
            content.innerHTML = html;
        },

        _buildBrowseGrid: function (items, source) {
            var html = '<div class="aw-browse-grid">';
            items.forEach(function (item) {
                var itemSource = item.Source || source || 'aniworld';
                html += '<div class="aw-browse-card" onclick="window.AW.showSeries(\'' + encodeURIComponent(item.Url) + '\', \'' + escJs(item.Title) + '\', \'' + escJs(itemSource) + '\')">';
                html += '<img class="aw-browse-cover" src="' + esc(item.CoverImageUrl) + '" alt="' + esc(item.Title) + '" loading="lazy" onerror="this.style.display=\'none\'" />';
                var badgeCls = 'aw-browse-source-badge' + (itemSource === 'aniworld' ? ' aw-badge-aniworld' : '');
                html += '<img class="' + badgeCls + '" src="' + siteLogoUrl(itemSource) + '" onerror="this.style.display=\'none\'" />';
                html += '<div class="aw-browse-info">';
                html += '<h3>' + esc(item.Title) + '</h3>';
                if (item.Genre) {
                    html += '<small>' + esc(item.Genre) + '</small>';
                }
                html += '</div></div>';
            });
            html += '</div>';
            return html;
        },

        // ── Search ──
        // "Search" always reads at the same width as "Search All": both buttons get a
        // min-width of the wider button's natural width, so the pair stays a matched
        // unit in every viewport. Re-synced when the layout or the fonts change.
        _syncSearchBtnWidths: function () {
            var search = view.querySelector('#aw-search-btn');
            var searchAll = view.querySelector('#aw-search-all-btn');
            if (!search || !searchAll) return;
            search.style.minWidth = '';
            searchAll.style.minWidth = '';
            var w = Math.max(search.offsetWidth, searchAll.offsetWidth);
            if (!w) return; // browse tab hidden: nothing to measure, re-syncs on show
            search.style.minWidth = w + 'px';
            searchAll.style.minWidth = w + 'px';
        },

        search: function () {
            this._runSearch(this._searchQuery(), false);
        },

        searchAll: function () {
            this._runSearch(this._searchQuery(), true);
        },

        _searchQuery: function () {
            var input = view.querySelector('#aw-search-input');
            return input ? input.value.trim() : '';
        },

        // Search the selected provider (all = every enabled provider at once)
        _runSearch: function (query, all) {
            if (!query) return;

            this.lastSearchQuery = query;
            this.lastSearchContext = { query: query, all: !!all };
            this.viewMode = 'search';

            var content = view.querySelector('#aw-content');
            var what = all ? ' all sources' : ' ' + (PROVIDER_NAMES[this.currentProvider] || '');
            content.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Searching' + what + '...</div>';

            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Search', { query: query, source: all ? 'all' : this.currentProvider }),
                type: 'GET',
                dataType: 'json'
            }).then(function (results) {
                AW.lastSearchResults = results;
                AW.renderSearchResults(results);
            }).catch(function (err) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="fa-solid fa-xmark-circle"></i></div>Search failed: ' + esc(err.message || 'Unknown error') + '</div>';
            });
        },

        searchGeneration: 0,

        renderSearchResults: function (results) {
            var content = view.querySelector('#aw-content');
            if (!results || results.length === 0) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="fa-solid fa-search"></i></div>No results found. Try different keywords.</div>';
                return;
            }

            this.searchGeneration++;
            var myGen = this.searchGeneration;

            var html = '<div class="aw-browse-grid">';
            results.forEach(function (item, idx) {
                var source = item.Source || 'aniworld';
                var cardId = 'aw-sr-' + idx;
                html += '<div class="aw-browse-card" id="' + cardId + '" onclick="window.AW.showSeries(\'' + encodeURIComponent(item.Url) + '\', \'' + escJs(item.Title) + '\', \'' + escJs(source) + '\')">';
                html += '<div class="aw-browse-cover aw-cover-placeholder" id="' + cardId + '-cover"></div>';
                var badgeCls = 'aw-browse-source-badge' + (source === 'aniworld' ? ' aw-badge-aniworld' : '');
                html += '<img class="' + badgeCls + '" src="' + siteLogoUrl(source) + '" onerror="this.style.display=\'none\'" />';
                html += '<div class="aw-browse-info aw-browse-info-solo">';
                html += '<h3>' + esc(item.Title) + '</h3>';
                html += '</div></div>';
            });
            html += '</div>';
            content.innerHTML = html;

            // Lazy-load all covers in parallel (no stagger)
            results.forEach(function (item, idx) {
                AW.fetchSearchCover(item.Url, item.Source || 'aniworld', 'aw-sr-' + idx, myGen);
            });
        },

        fetchSearchCover: function (seriesUrl, source, cardId, generation) {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Series', { url: seriesUrl, source: source }),
                type: 'GET',
                dataType: 'json'
            }).then(function (series) {
                if (generation !== undefined && AW.searchGeneration !== generation) return;
                var coverEl = view.querySelector('#' + cardId + '-cover');
                if (!coverEl) return;
                if (series.CoverImageUrl) {
                    var img = document.createElement('img');
                    img.className = 'aw-browse-cover';
                    img.src = series.CoverImageUrl;
                    img.alt = series.Title || '';
                    img.loading = 'lazy';
                    img.onerror = function () { this.style.display = 'none'; };
                    coverEl.parentNode.replaceChild(img, coverEl);
                }
            }).catch(function () { /* keep placeholder */ });
        },

        // ── Series Detail ──
        showSeries: function (encodedUrl, title, source) {
            var url = decodeURIComponent(encodedUrl);
            this.currentSeriesUrl = url;
            this.currentSeriesSource = source || 'aniworld';
            this.viewMode = 'series';

            var content = view.querySelector('#aw-content');
            content.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading series info...</div>';

            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Series', { url: url, source: this.currentSeriesSource }),
                type: 'GET',
                dataType: 'json'
            }).then(function (series) {
                AW.currentSeriesTitle = series.Title || title || 'Unknown';
                AW.renderSeries(series, url);
            }).catch(function (err) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="fa-solid fa-xmark-circle"></i></div>Failed to load series: ' + esc(err.message || 'Unknown error') + '</div>';
            });
        },

        renderSeries: function (series, seriesUrl) {
            var content = view.querySelector('#aw-content');
            var source = this.currentSeriesSource || 'aniworld';
            var html = '<button class="aw-btn aw-btn-secondary aw-back" onclick="window.AW.goBack()"><i class="fa-solid fa-arrow-left"></i> Back</button>';

            if (source === 'sto' && series.Genres && series.Genres.some(function (g) { return g.toLowerCase() === 'anime'; })) {
                html += '<div class="aw-warning"><i class="fa-solid fa-triangle-exclamation"></i> For anime, using AniWorld as source is recommended.</div>';
            }

            html += '<div class="aw-series">';
            if (series.CoverImageUrl) {
                html += '<img class="aw-cover" src="' + esc(series.CoverImageUrl) + '" alt="Cover" onerror="this.style.display=\'none\'" />';
            }
            html += '<div class="aw-meta">';
            html += '<h2><img class="aw-source-logo" src="' + siteLogoUrl(source) + '" onerror="this.style.display=\'none\'" style="height:1.3em"> ' + esc(series.Title) + '</h2>';

            if (series.Genres && series.Genres.length > 0) {
                html += '<div class="aw-genres">';
                series.Genres.forEach(function (g) {
                    html += '<span class="aw-genre">' + esc(g) + '</span>';
                });
                html += '</div>';
            }

            if (series.Description) {
                var desc = series.Description;
                if (desc.length > 300) {
                    html += '<p>' + esc(desc.substring(0, 300)) + '...</p>';
                } else {
                    html += '<p>' + esc(desc) + '</p>';
                }
            }
            html += '</div></div>';

            if (series.Seasons && series.Seasons.length > 0) {
                html += '<div class="aw-series-actions">';
                html += '<select id="aw-season-select" class="aw-season-select" title="Select season" onchange="window.AW.loadSeason(this.value)">';
                series.Seasons.forEach(function (season) {
                    html += '<option value="' + encodeURIComponent(season.Url) + '">Season ' + esc(String(season.Number)) + '</option>';
                });
                if (series.HasMovies) {
                    html += '<option value="' + encodeURIComponent(seriesUrl + '/filme') + '">\uD83C\uDFAC Movies</option>';
                }
                html += '</select>';
                html += '</div>';
            }

            html += '<div id="aw-season-bar"></div>';
            html += '<div id="aw-episodes"></div>';
            content.innerHTML = html;

            if (series.Seasons && series.Seasons.length > 0) {
                AW.loadSeason(encodeURIComponent(series.Seasons[0].Url));
            }
        },

        // ── Season Episodes ──
        loadSeason: function (encodedUrl) {
            // Keep the picker in sync when loadSeason is called programmatically.
            var picker = view.querySelector('#aw-season-select');
            if (picker && picker.value !== encodedUrl) picker.value = encodedUrl;

            this.seasonGeneration++;
            var myGeneration = this.seasonGeneration;

            var url = decodeURIComponent(encodedUrl);
            this.currentSeasonUrl = url;
            var epContainer = view.querySelector('#aw-episodes');
            var barContainer = view.querySelector('#aw-season-bar');
            if (!epContainer) return;

            epContainer.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading episodes...</div>';
            if (barContainer) barContainer.innerHTML = '';

            var source = this.currentSeriesSource || 'aniworld';
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Episodes', { url: url, source: source }),
                type: 'GET',
                dataType: 'json'
            }).then(function (episodes) {
                if (AW.seasonGeneration !== myGeneration) return;
                AW.renderEpisodes(episodes, url, myGeneration);
            }).catch(function (err) {
                if (AW.seasonGeneration !== myGeneration) return;
                epContainer.innerHTML = '<div class="aw-empty">Failed to load episodes: ' + esc(err.message || '') + '</div>';
            });
        },

        renderEpisodes: function (episodes, seasonUrl, generation) {
            var epContainer = view.querySelector('#aw-episodes');
            var barContainer = view.querySelector('#aw-season-bar');
            var source = this.currentSeriesSource || 'aniworld';

            if (!episodes || episodes.length === 0) {
                epContainer.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="fa-solid fa-inbox"></i></div>No episodes found.</div>';
                if (barContainer) barContainer.innerHTML = '';
                return;
            }

            if (barContainer) {
                var bar = '<div class="aw-season-actions">';
                bar += '<div class="aw-season-meta">';
                bar += '<span class="aw-ep-count">' + episodes.length + ' episode' + (episodes.length === 1 ? '' : 's') + '</span>';
                bar += '<select id="aw-season-lang" class="aw-lang-select" title="Language for downloads">';
                bar += getLangOptionsHtml(source);
                bar += '</select>';
                bar += '</div>';
                bar += '<div class="aw-season-opts">';
                bar += '<label class="aw-check" title="Priority downloads are added to the front of the queue"><input type="checkbox" id="aw-priority-cb"><span>Priority</span></label>';
                bar += '<label class="aw-check" title="Redownload episodes even if they are already flagged as downloaded"><input type="checkbox" id="aw-force-cb"><span>Force</span></label>';
                bar += '</div>';
                bar += '<div class="aw-season-btns">';
                bar += '<button class="aw-btn aw-btn-success aw-btn-sm" onclick="window.AW.downloadSeason(\'' + encodeURIComponent(seasonUrl) + '\')"><i class="fa-solid fa-download"></i> Download Season</button>';
                if (AW.currentSeriesUrl) {
                    bar += '<button class="aw-btn aw-btn-all-seasons aw-btn-sm" onclick="window.AW.downloadAllSeasons(\'' + encodeURIComponent(AW.currentSeriesUrl) + '\')"><i class="fa-solid fa-download"></i> Download All Seasons</button>';
                }
                bar += '</div>';
                bar += '</div>';

                barContainer.innerHTML = bar;

                // Enforce "Only German Sub and German Dub" setting for AniWorld
                if (source === 'aniworld' && AW.aniWorldOnlyGerman) {
                    var langSel2 = view.querySelector('#aw-season-lang');
                    if (langSel2) {
                        // Remove the English Sub option (value="2")
                        var engOpt = langSel2.querySelector('option[value="2"]');
                        if (engOpt) engOpt.remove();
                        // If current value was English Sub, switch to German Dub
                        if (langSel2.value === '2') langSel2.value = '1';
                        langSel2.title = 'English Sub disabled by admin setting';
                    }
                }
            }

            var html = '<div class="aw-episodes">';
            episodes.forEach(function (ep) {
                var label = ep.IsMovie ? 'Movie ' + ep.Number : ep.Number;
                var epId = 'ep-' + ep.Number + '-' + (ep.IsMovie ? 'movie' : 'ep');
                html += '<div class="aw-ep" id="' + epId + '">';
                html += '<span class="aw-ep-num">' + label + '</span>';
                html += '<span class="aw-ep-title" id="' + epId + '-title">Loading...</span>';
                html += '<span class="aw-ep-downloaded" id="' + epId + '-dl" style="display:none"></span>';
                html += '<div class="aw-ep-actions">';
                html += '<button class="aw-btn aw-btn-primary aw-btn-sm" onclick="window.AW.downloadEpisode(\'' + encodeURIComponent(ep.Url) + '\')"><i class="fa-solid fa-download"></i> Download</button>';
                html += '<button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.toggleProviders(\'' + encodeURIComponent(ep.Url) + '\', \'' + epId + '\')">Providers</button>';
                html += '</div>';
                html += '</div>';
                html += '<div id="' + epId + '-providers" class="aw-ep-providers" style="display:none"></div>';
            });
            html += '</div>';
            epContainer.innerHTML = html;

            var myGen = generation || AW.seasonGeneration;
            episodes.forEach(function (ep, idx) {
                var epId = 'ep-' + ep.Number + '-' + (ep.IsMovie ? 'movie' : 'ep');
                setTimeout(function () {
                    if (AW.seasonGeneration !== myGen) return;
                    AW.fetchEpisodeTitle(ep.Url, epId, myGen);
                    AW.checkIsDownloaded(ep.Url, epId);
                }, idx * 150);
            });
        },

        fetchEpisodeTitle: function (url, epId, generation) {
            var titleEl = view.querySelector('#' + epId + '-title');
            if (!titleEl) return;

            var source = this.currentSeriesSource || 'aniworld';
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Episode', { url: url, source: source }),
                type: 'GET',
                dataType: 'json'
            }).then(function (details) {
                if (generation !== undefined && AW.seasonGeneration !== generation) return;
                titleEl = view.querySelector('#' + epId + '-title');
                if (!titleEl) return;
                var title = details.TitleEn || details.TitleDe || '';
                if (details.TitleDe && details.TitleEn && details.TitleDe !== details.TitleEn) {
                    titleEl.textContent = details.TitleEn + ' \u2014 ' + details.TitleDe;
                } else {
                    titleEl.textContent = title || '\u2014';
                }
            }).catch(function () {
                if (generation !== undefined && AW.seasonGeneration !== generation) return;
                titleEl = view.querySelector('#' + epId + '-title');
                if (titleEl) titleEl.textContent = '\u2014';
            });
        },

        checkIsDownloaded: function (url, epId) {
            var source = this.currentSeriesSource || 'aniworld';
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/IsDownloaded', { url: url, title: this.currentSeriesTitle || 'Unknown' }),
                type: 'GET',
                dataType: 'json'
            }).then(function (result) {
                if (result && result.downloaded && result.languages && result.languages.length > 0) {
                    var badge = view.querySelector('#' + epId + '-dl');
                    if (badge) {
                        var html = '<i class="fa-solid fa-check"></i>';
                        for (var i = 0; i < result.languages.length; i++) {
                            html += '<img src="' + ApiClient.getUrl('AniWorld/Flag/' + result.languages[i], { source: source }) + '" style="height:1.1em;vertical-align:middle;margin-right:0.2em">';
                        }
                        badge.innerHTML = html;
                        badge.style.display = '';
                    }
                }
            }).catch(function () { /* ignore */ });
        },

        // ── Providers ──
        toggleProviders: function (encodedUrl, epId) {
            var panel = view.querySelector('#' + epId + '-providers');
            if (!panel) return;

            if (panel.style.display !== 'none') {
                panel.style.display = 'none';
                return;
            }

            panel.style.display = '';
            panel.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading...</div>';

            var url = decodeURIComponent(encodedUrl);
            var source = this.currentSeriesSource || 'aniworld';
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Episode', { url: url, source: source }),
                type: 'GET',
                dataType: 'json'
            }).then(function (details) {
                var html = '';

                var hasAny = false;
                for (var langKey in details.ProvidersByLanguage) {
                    // Skip blocked languages based on admin settings
                    if (source === 'aniworld' && AW.aniWorldOnlyGerman && langKey === '2') continue;

                    hasAny = true;
                    html += '<div class="aw-lang-group">';
                    html += '<div class="aw-lang-label">' + langLabelHtml(langKey, source) + '</div>';
                    html += '<div class="aw-provider-btns">';
                    var providers = details.ProvidersByLanguage[langKey];
                    for (var prov in providers) {
                        html += '<button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.downloadWithOptions(\'' + encodeURIComponent(url) + '\', \'' + escJs(langKey) + '\', \'' + escJs(prov) + '\')">' + esc(prov) + '</button>';
                    }
                    html += '</div></div>';
                }

                if (!hasAny) {
                    html = '<div style="opacity:0.5;padding:0.5em">No providers available for this episode.</div>';
                }

                panel.innerHTML = html;
            }).catch(function () {
                panel.innerHTML = '<div style="color:#ef5350;padding:0.5em">Failed to load providers.</div>';
            });
        },

        // ── Downloads ──
        _isPriorityChecked: function () {
            var cb = view.querySelector('#aw-priority-cb');
            return cb ? cb.checked : false;
        },

        _isForceChecked: function () {
            var cb = view.querySelector('#aw-force-cb');
            return cb ? cb.checked : false;
        },

        _checkMaintenance: function () {
            if (this.maintenanceMode) {
                Dashboard.alert('Downloads are blocked: maintenance mode is active.');
                return true;
            }
            return false;
        },

        downloadEpisode: function (encodedUrl) {
            if (this._checkMaintenance()) return;
            var url = decodeURIComponent(encodedUrl);
            var langSelect = view.querySelector('#aw-season-lang');
            var lang = (langSelect && langSelect.value) ? langSelect.value : null;
            this._startDownload(url, lang, null);
        },

        downloadWithOptions: function (encodedUrl, langKey, provider) {
            if (this._checkMaintenance()) return;
            var url = decodeURIComponent(encodedUrl);
            this._startDownload(url, langKey, provider);
        },

        downloadSeason: function (encodedSeasonUrl) {
            if (this._checkMaintenance()) return;
            var seasonUrl = decodeURIComponent(encodedSeasonUrl);

            var body = {
                SeasonUrl: seasonUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };

            if (this._isPriorityChecked()) body.Priority = true;
            if (this._isForceChecked()) body.Force = true;

            var langSelect = view.querySelector('#aw-season-lang');
            if (langSelect && langSelect.value) {
                body.LanguageKey = langSelect.value;
            }

            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/DownloadSeason'),
                type: 'POST',
                data: JSON.stringify(body),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (tasks) {
                var count = tasks ? tasks.length : 0;
                if (count > 0) {
                    Dashboard.alert('Queued ' + count + ' episode(s) for download!');
                    AW.switchTab('downloads');
                } else {
                    Dashboard.alert('All episodes already downloaded or no episodes found.');
                }
            }).catch(function (err) {
                AW._handleApiError(err, 'Batch download failed');
            });
        },

        downloadAllSeasons: function (encodedSeriesUrl) {
            if (this._checkMaintenance()) return;
            var seriesUrl = decodeURIComponent(encodedSeriesUrl);

            var body = {
                SeriesUrl: seriesUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };

            if (this._isPriorityChecked()) body.Priority = true;
            if (this._isForceChecked()) body.Force = true;

            var langSelect = view.querySelector('#aw-season-lang');
            if (langSelect && langSelect.value) {
                body.LanguageKey = langSelect.value;
            }

            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/DownloadAll'),
                type: 'POST',
                data: JSON.stringify(body),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (result) {
                var msg = 'Queued ' + result.queued + ' episode(s) across ' + result.seasons + ' season(s)!';
                if (result.skipped > 0) {
                    msg += ' (' + result.skipped + ' already downloaded)';
                }
                if (result.queued > 0) {
                    Dashboard.alert(msg);
                    AW.switchTab('downloads');
                } else {
                    Dashboard.alert('All episodes already downloaded!');
                }
            }).catch(function (err) {
                AW._handleApiError(err, 'Download all failed');
            });
        },

        _startDownload: function (episodeUrl, langKey, provider) {
            var body = {
                EpisodeUrl: episodeUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };
            if (langKey) body.LanguageKey = langKey;
            if (provider) body.Provider = provider;
            if (this._isPriorityChecked()) body.Priority = true;
            if (this._isForceChecked()) body.Force = true;

            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Download'),
                type: 'POST',
                data: JSON.stringify(body),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (task) {
                Dashboard.alert('Download started: ' + (task.EpisodeTitle || task.OutputPath || task.Id));
                AW.updateBadge(AW.activeDownloadCount + 1);
            }).catch(function (err) {
                AW._handleApiError(err, 'Download failed');
            });
        },

        _handleApiError: function (err, prefix) {
            if (err && typeof err.json === 'function') {
                err.json().then(function (body) {
                    var msg = body.detail || body.title || body.error || JSON.stringify(body);
                    Dashboard.alert(prefix + ': ' + msg);
                }).catch(function () {
                    Dashboard.alert(prefix + ': HTTP ' + (err.status || 'error'));
                });
            } else {
                Dashboard.alert(prefix + ': ' + (err.message || 'Unknown error'));
            }
        },

        // ── Downloads Tab ──
        loadDownloads: function () {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Downloads'),
                type: 'GET',
                dataType: 'json'
            }).then(function (downloads) {
                AW.renderDownloads(downloads);
            }).catch(function () {
                var container = view.querySelector('#aw-downloads');
                if (container) container.innerHTML = '<div class="aw-empty">Failed to load downloads.</div>';
            });
        },

        renderDownloads: function (downloads) {
            var container = view.querySelector('#aw-downloads');
            if (!container) return;

            var active = 0;
            if (downloads) {
                downloads.forEach(function (dl) {
                    if (['Queued', 'Resolving', 'Extracting', 'Downloading', 'Retrying'].indexOf(dl.Status) !== -1) {
                        active++;
                    }
                });
            }
            AW.activeDownloadCount = active;
            AW.updateBadge(active);

            if (!downloads || downloads.length === 0) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="fa-solid fa-inbox"></i></div>No active downloads.<br>Search for anime and start downloading!</div>';
                return;
            }

            var statusOrder = { Downloading: 0, Retrying: 0, Extracting: 0, Resolving: 0, Queued: 1, Completed: 2, Failed: 2, Cancelled: 2 };
            downloads.sort(function (a, b) {
                var sa = statusOrder[a.Status] !== undefined ? statusOrder[a.Status] : 9;
                var sb = statusOrder[b.Status] !== undefined ? statusOrder[b.Status] : 9;
                if (sa !== sb) return sa - sb;
                if (sa === 2) {
                    // Done group: newest first
                    return (b.StartedAt || '').localeCompare(a.StartedAt || '');
                }
                // Active (0) and Queued (1): preserve backend insertion order (next-up first)
                return 0;
            });

            var hasCompleted = downloads.some(function (dl) {
                return ['Completed', 'Failed', 'Cancelled'].indexOf(dl.Status) !== -1;
            });

            var html = '';
            if (hasCompleted) {
                html += '<div class="aw-dl-actions"><button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.clearCompleted()"><i class="fa-solid fa-broom"></i> Clear Completed</button></div>';
            }

            html += '<div class="aw-dl">';
            downloads.forEach(function (dl) {
                var statusCls = 'aw-status-' + dl.Status.toLowerCase();
                var isFailed = dl.Status === 'Failed';
                var isActive = ['Queued', 'Resolving', 'Extracting', 'Downloading', 'Retrying'].indexOf(dl.Status) !== -1;
                var fileName = dl.OutputPath ? dl.OutputPath.split('/').pop().split('\\').pop() : dl.Id;
                var dlSource = dl.Source || 'aniworld';

                html += '<div class="aw-dl-item">';
                html += '<div class="aw-dl-info">';
                html += '<strong><img class="aw-source-logo" src="' + siteLogoUrl(dlSource) + '" onerror="this.style.display=\'none\'" style="height:1em"> ' + esc(dl.EpisodeTitle || fileName) + '</strong>';
                var langNames = getLangNames(dlSource);
                var langLabel = langNames[dl.Language] || dl.Language || '';
                var metaParts = [esc(dl.Provider)];
                if (langLabel) metaParts.push(esc(langLabel));
                if (dl.Username) metaParts.push(esc(dl.Username));
                html += '<small>' + metaParts.join(' \u00B7 ');
                if (dl.RetryCount > 0) {
                    html += ' (retry ' + dl.RetryCount + '/' + dl.MaxRetries + ')';
                }
                if (dl.FileSizeBytes > 0) {
                    html += '<span class="aw-dl-size">' + formatSize(dl.FileSizeBytes) + '</span>';
                }
                html += '</small>';
                if (dl.Error && dl.Status !== 'Retrying') {
                    html += '<div class="aw-dl-error">' + esc(dl.Error) + '</div>';
                }
                if (dl.Status === 'Retrying' && dl.Error) {
                    html += '<div class="aw-dl-retry-info"><i class="fa-solid fa-hourglass-half"></i> ' + esc(dl.Error) + '</div>';
                }
                if (dl.LanguageFallbackNote) {
                    html += '<div class="aw-dl-retry-info">' + esc(dl.LanguageFallbackNote) + '</div>';
                }
                html += '</div>';

                html += '<div class="aw-dl-progress"><div class="aw-dl-bar" style="width:' + dl.Progress + '%"></div></div>';
                html += '<span class="aw-dl-pct">' + dl.Progress + '%</span>';
                html += '<span class="aw-status ' + statusCls + '">' + esc(dl.Status) + '</span>';

                html += '<div class="aw-dl-btns">';
                if (isActive) {
                    html += '<button class="aw-btn aw-btn-danger aw-btn-sm" onclick="window.AW.cancelDownload(\'' + dl.Id + '\')" title="Cancel"><i class="fa-solid fa-xmark"></i></button>';
                }
                if (isFailed) {
                    html += '<button class="aw-btn aw-btn-warning aw-btn-sm" onclick="window.AW.retryDownload(\'' + dl.Id + '\')" title="Retry"><i class="fa-solid fa-rotate-right"></i></button>';
                }
                html += '</div>';

                html += '</div>';
            });
            html += '</div>';

            container.innerHTML = html;
        },

        cancelDownload: function (id) {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Downloads/' + id),
                type: 'DELETE'
            }).then(function () {
                AW.loadDownloads();
            });
        },

        retryDownload: function (id) {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Downloads/' + id + '/Retry'),
                type: 'POST'
            }).then(function () {
                Dashboard.alert('Retrying download...');
                AW.loadDownloads();
            }).catch(function (err) {
                Dashboard.alert('Retry failed: ' + (err.message || 'Unknown error'));
            });
        },

        clearCompleted: function () {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Downloads/Clear'),
                type: 'POST'
            }).then(function () {
                AW.loadDownloads();
            });
        },

        updateBadge: function (count) {
            var badge = view.querySelector('#aw-dl-badge');
            if (badge) {
                if (count > 0) {
                    badge.textContent = count;
                    badge.style.display = '';
                } else {
                    badge.style.display = 'none';
                }
            }
        },

        startPolling: function () {
            this.stopPolling();
            this.downloadPollInterval = setInterval(function () {
                AW.loadDownloads();
            }, 2500);
        },

        stopPolling: function () {
            if (this.downloadPollInterval) {
                clearInterval(this.downloadPollInterval);
                this.downloadPollInterval = null;
            }
        },

        // ── History Tab ──
        loadStats: function () {
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Stats'),
                type: 'GET',
                dataType: 'json'
            }).then(function (stats) {
                AW.renderStats(stats);
            }).catch(function () {
                var container = view.querySelector('#aw-history-stats');
                if (container) container.innerHTML = '';
            });
        },

        renderStats: function (stats) {
            var container = view.querySelector('#aw-history-stats');
            if (!container) return;

            var html = '<div class="aw-stats">';
            html += '<div class="aw-stat"><div class="aw-stat-value">' + formatCount(stats.TotalDownloads) + '</div><div class="aw-stat-label">Total Downloads</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value green">' + formatCount(stats.Completed) + '</div><div class="aw-stat-label">Completed</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value red">' + formatCount(stats.Failed) + '</div><div class="aw-stat-label">Failed</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value">' + formatSize(stats.TotalBytes) + '</div><div class="aw-stat-label">Total Size</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value orange">' + formatCount(stats.UniqueSeriesCount) + '</div><div class="aw-stat-label">Series</div></div>';
            html += '</div>';
            container.innerHTML = html;
        },

        loadHistory: function (reset) {
            if (reset) {
                this.historyOffset = 0;
            }

            var params = { limit: 30, offset: this.historyOffset };
            if (this.historyStatusFilter) params.status = this.historyStatusFilter;
            if (this.historySeriesFilter) params.series = this.historySeriesFilter;

            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/History', params),
                type: 'GET',
                dataType: 'json'
            }).then(function (records) {
                AW.renderHistory(records, reset);
                AW.renderHistoryFilters();
            }).catch(function () {
                var container = view.querySelector('#aw-history');
                if (container) container.innerHTML = '<div class="aw-empty">Failed to load history.</div>';
            });
        },

        renderHistoryFilters: function () {
            var container = view.querySelector('#aw-history-filters-container');
            if (!container) return;

            if (container.dataset.rendered === 'true') return;
            container.dataset.rendered = 'true';

            var html = '<div class="aw-hist-filters">';
            html += '<select id="aw-hist-status" onchange="window.AW.filterHistory()">';
            html += '<option value="">All Status</option>';
            html += '<option value="Completed">\u2705 Completed</option>';
            html += '<option value="Failed">\u274C Failed</option>';
            html += '<option value="Cancelled">\u26D4 Cancelled</option>';
            html += '</select>';
            html += '<input type="text" id="aw-hist-series" class="aw-hist-input" placeholder="Filter by series..." />';
            html += '<button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.filterHistory()">Filter</button>';
            html += '</div>';
            container.innerHTML = html;
        },

        filterHistory: function () {
            var statusEl = view.querySelector('#aw-hist-status');
            var seriesEl = view.querySelector('#aw-hist-series');
            this.historyStatusFilter = statusEl ? statusEl.value || null : null;
            this.historySeriesFilter = seriesEl ? seriesEl.value.trim() || null : null;
            this.loadHistory(true);
        },

        renderHistory: function (records, reset) {
            var container = view.querySelector('#aw-history');
            if (!container) return;

            if ((!records || records.length === 0) && reset) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon"><i class="fa-solid fa-inbox"></i></div>No download history yet.<br>Downloaded episodes will appear here.</div>';
                return;
            }

            var html = reset ? '<div class="aw-history">' : '';

            records.forEach(function (rec) {
                var statusCls = 'aw-status-' + rec.Status.toLowerCase();
                var title = rec.EpisodeTitle || '';
                var seLabel = 'S' + String(rec.Season).padStart(2, '0') + 'E' + String(rec.Episode).padStart(2, '0');
                var recSource = rec.Source || 'aniworld';
                var langNames = getLangNames(recSource);

                html += '<div class="aw-hist-item">';
                html += '<div class="aw-hist-info">';
                html += '<strong><img class="aw-source-logo" src="' + siteLogoUrl(recSource) + '" onerror="this.style.display=\'none\'" style="height:1em"> ' + esc(rec.SeriesTitle) + ' ' + seLabel;
                if (title) html += ' - ' + esc(title);
                html += '</strong>';
                html += '<small>' + esc(rec.Provider) + ' \u00B7 ' + esc(langNames[rec.Language] || rec.Language);
                if (rec.Error) html += ' \u00B7 ' + esc(rec.Error.substring(0, 60));
                html += '</small>';
                html += '</div>';
                html += '<div class="aw-hist-meta">';
                if (rec.FileSizeBytes > 0) {
                    html += '<span class="aw-hist-size">' + formatSize(rec.FileSizeBytes) + '</span>';
                }
                html += '<span class="aw-status ' + statusCls + '">' + esc(rec.Status) + '</span>';
                html += '<span class="aw-hist-date">' + formatDate(rec.StartedAt) + '</span>';
                html += '</div>';
                html += '</div>';
            });

            if (reset) {
                html += '</div>';
                container.innerHTML = html;
            } else {
                var histDiv = container.querySelector('.aw-history');
                if (histDiv) {
                    histDiv.insertAdjacentHTML('beforeend', html);
                }
            }

            var moreContainer = container.querySelector('.aw-hist-more');
            if (moreContainer) moreContainer.remove();

            if (records && records.length >= 30) {
                AW.historyOffset += records.length;
                container.insertAdjacentHTML('beforeend', '<div class="aw-hist-more"><button class="aw-btn aw-btn-secondary" onclick="window.AW.loadHistory(false)">Load More</button></div>');
            }
        },

        goBack: function () {
            this.currentSeriesUrl = null;
            this.currentSeriesSource = null;
            if (this.lastSearchContext) {
                // Return to the last search results
                this._runSearch(this.lastSearchContext.query, this.lastSearchContext.all);
            } else {
                // Otherwise back to the selected provider's browse rows
                this.showProviderBrowse(this.currentProvider);
            }
        }
    };

    // Expose globally for onclick handlers in dynamic HTML
    window.AW = AW;

    // Load settings from server (enabled providers, language restrictions, maintenance mode)
    ApiClient.fetch({
        url: ApiClient.getUrl('AniWorld/EnabledSources'),
        type: 'GET',
        dataType: 'json'
    }).then(function (sources) {
        AW.aniWorldOnlyGerman = sources.aniWorldOnlyGerman === true;
        AW.maintenanceMode = sources.maintenanceMode === true;
        if (AW.maintenanceMode) {
            var banner = view.querySelector('#aw-maintenance-banner');
            var text = view.querySelector('#aw-maintenance-text');
            if (banner && text) {
                text.textContent = sources.maintenanceMessage || 'The downloader is currently under maintenance.';
                banner.style.display = '';
            }
        }

        AW.enabledSources = {
            aniworld: sources.aniworld !== false,
            sto: sources.sto === true,
            filmo: sources.filmo === true,
            filmpalast: sources.filmpalast === true,
            megakino: sources.megakino === true,
            moflix: sources.moflix === true
        };
        AW.buildProviderControl();
        AW.showCurrentView();
    }).catch(function () {
        // Without the settings the page should still work with the defaults (AniWorld only)
        AW.buildProviderControl();
        AW.showCurrentView();
    });

    // Hide settings button when opened from sidebar (non-admin view)
    if (params && params.sidebar) {
        var settingsBtn = view.querySelector('#aw-settings-btn');
        if (settingsBtn) {
            settingsBtn.style.display = 'none';
        }
    }

    // Bind Enter key to search
    var searchInput = view.querySelector('#aw-search-input');
    if (searchInput) {
        searchInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                AW.search();
            }
        });
    }

    // Keep both search buttons the same width (see _syncSearchBtnWidths).
    AW._syncSearchBtnWidths();
    var searchBar = view.querySelector('.aw-search-bar');
    if (searchBar && typeof ResizeObserver !== 'undefined') {
        // Observing the row (not the buttons): their min-width doesn't change the
        // row's size, so this can't feed back into itself.
        new ResizeObserver(function () { AW._syncSearchBtnWidths(); }).observe(searchBar);
    }
    if (document.fonts && document.fonts.ready) {
        document.fonts.ready.then(function () { AW._syncSearchBtnWidths(); });
    }

    // Poll badge count periodically
    var badgePollInterval = setInterval(function () {
        ApiClient.fetch({
            url: ApiClient.getUrl('AniWorld/Downloads'),
            type: 'GET',
            dataType: 'json'
        }).then(function (downloads) {
            var active = 0;
            if (downloads) {
                downloads.forEach(function (dl) {
                    if (['Queued', 'Resolving', 'Extracting', 'Downloading', 'Retrying'].indexOf(dl.Status) !== -1) {
                        active++;
                    }
                });
            }
            AW.updateBadge(active);
        }).catch(function () { /* ignore */ });
    }, 10000);

    // Cleanup when navigating away
    view.addEventListener('viewhide', function () {
        AW.stopPolling();
        if (badgePollInterval) {
            clearInterval(badgePollInterval);
            badgePollInterval = null;
        }
    });
}
