export default function (view, params) {

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
        return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
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

    // Language names per source (plain text)
    function getLangNames(source) {
        if (source === 'hianime') {
            return { 'sub': 'English Sub', 'dub': 'English Dub' };
        }
        if (source === 'sto') {
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
        if (source === 'hianime') {
            var html = '<option value="">\uD83C\uDF10 Use Settings Default</option>';
            html += '<option value="sub">\uD83C\uDDEC\uD83C\uDDE7 English Sub</option>';
            html += '<option value="dub">\uD83C\uDDEC\uD83C\uDDE7 English Dub</option>';
            return html;
        }
        var html = '<option value="">\uD83C\uDF10 Use Settings Default</option>';
        html += '<option value="1">\uD83C\uDDE9\uD83C\uDDEA German Dub</option>';
        if (source === 'sto') {
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

        episodeOffset: 0,
        existingFoldersLoaded: false,
        hiAnimeOnlyDub: false,
        aniWorldOnlyGerman: false,

        browseLoaded: { popular: false, new: false },

        // ── Tab switching ──
        switchTab: function (tab) {
            view.querySelectorAll('.aw-tab').forEach(function (t) { t.classList.remove('active'); });
            view.querySelector('[data-tab="' + tab + '"]').classList.add('active');
            view.querySelector('#searchTab').style.display = tab === 'search' ? '' : 'none';
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

            if (tab === 'search' && this.currentSeriesUrl) {
                this.showSeries(encodeURIComponent(this.currentSeriesUrl), this.currentSeriesTitle, this.currentSeriesSource);
            }

            if (tab === 'browse' && !this.browseLoaded.popular) {
                this.loadBrowseSection('popular');
            }
        },

        // ── Browse ──
        switchBrowseSection: function (section, btn) {
            view.querySelectorAll('.aw-browse-pill').forEach(function (b) { b.classList.remove('active'); });
            if (btn) btn.classList.add('active');
            this.loadBrowseSection(section);
        },

        loadBrowseSection: function (section) {
            var container = view.querySelector('#aw-browse-content');
            if (!container) return;

            if (this.browseLoaded[section]) {
                this._renderBrowseCombined(section, container);
                return;
            }

            container.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Loading...</div>';

            var endpoint = section === 'new' ? 'AniWorld/New' : 'AniWorld/Popular';
            var promises = [];

            // Load from aniworld
            promises.push(ApiClient.fetch({
                url: ApiClient.getUrl(endpoint, { source: 'aniworld' }),
                type: 'GET', dataType: 'json'
            }).catch(function () { return []; }));

            // Load from s.to and hianime if enabled (uses EnabledSources endpoint)
            promises.push(ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/EnabledSources'),
                type: 'GET', dataType: 'json'
            }).then(function (sources) {
                if (sources.sto) {
                    return ApiClient.fetch({
                        url: ApiClient.getUrl(endpoint, { source: 'sto' }),
                        type: 'GET', dataType: 'json'
                    }).catch(function () { return []; });
                }
                return [];
            }).catch(function () { return []; }));

            promises.push(ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/EnabledSources'),
                type: 'GET', dataType: 'json'
            }).then(function (sources) {
                if (sources.hianime) {
                    return ApiClient.fetch({
                        url: ApiClient.getUrl(endpoint, { source: 'hianime' }),
                        type: 'GET', dataType: 'json'
                    }).catch(function () { return []; });
                }
                return [];
            }).catch(function () { return []; }));

            Promise.all(promises).then(function (results) {
                AW.browseLoaded[section] = true;
                AW['browseCache_aniworld_' + section] = results[0] || [];
                AW['browseCache_sto_' + section] = results[1] || [];
                AW['browseCache_hianime_' + section] = results[2] || [];
                AW._renderBrowseCombined(section, container);
            }).catch(function (err) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">❌</div>Failed to load: ' + esc(err.message || 'Unknown error') + '</div>';
            });
        },

        _renderBrowseCombined: function (section, container) {
            var awItems = this['browseCache_aniworld_' + section] || [];
            var stoItems = this['browseCache_sto_' + section] || [];
            var hiItems = this['browseCache_hianime_' + section] || [];
            var html = '';

            if (awItems.length === 0 && stoItems.length === 0 && hiItems.length === 0) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">📭</div>No content found.</div>';
                return;
            }

            if (awItems.length > 0) {
                html += '<div class="aw-browse-section-title">AniWorld</div>';
                html += this._buildBrowseGrid(awItems, 'aniworld');
            }

            if (hiItems.length > 0) {
                html += '<div class="aw-browse-section-title">HiAnime</div>';
                html += this._buildBrowseGrid(hiItems, 'hianime');
            }

            if (stoItems.length > 0) {
                html += '<div class="aw-browse-section-title">s.to</div>';
                html += this._buildBrowseGrid(stoItems, 'sto');
            }

            container.innerHTML = html;
        },

        _buildBrowseGrid: function (items, source) {
            var html = '<div class="aw-browse-grid">';
            items.forEach(function (item) {
                var itemSource = item.Source || source || 'aniworld';
                html += '<div class="aw-browse-card" onclick="window.AW.showSeries(\'' + encodeURIComponent(item.Url) + '\', \'' + escJs(item.Title) + '\', \'' + escJs(itemSource) + '\')">';
                html += '<img class="aw-browse-cover" src="' + esc(item.CoverImageUrl) + '" alt="' + esc(item.Title) + '" loading="lazy" onerror="this.style.display=\'none\'" />';
                if (itemSource === 'hianime') {
                    html += '<span class="aw-browse-source-badge aw-badge-hianime"><img src="' + siteLogoUrl(itemSource) + '" onerror="this.parentNode.style.display=\'none\'" /></span>';
                } else {
                    var badgeCls = 'aw-browse-source-badge' + (itemSource === 'aniworld' ? ' aw-badge-aniworld' : '');
                    html += '<img class="' + badgeCls + '" src="' + siteLogoUrl(itemSource) + '" onerror="this.style.display=\'none\'" />';
                }
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

        renderBrowseItems: function (items, container) {
            if (!items || items.length === 0) {
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">📭</div>No content found.</div>';
                return;
            }

            container.innerHTML = this._buildBrowseGrid(items, 'aniworld');
        },

        // ── Search ──
        search: function () {
            var query = view.querySelector('#aw-search-input').value.trim();
            if (!query) return;

            this.lastSearchQuery = query;
            var content = view.querySelector('#aw-content');
            content.innerHTML = '<div class="aw-loading"><span class="aw-spinner"></span> Searching...</div>';

            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/Search', { query: query }),
                type: 'GET',
                dataType: 'json'
            }).then(function (results) {
                AW.lastSearchResults = results;
                AW.renderSearchResults(results);
            }).catch(function (err) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">\u274C</div>Search failed: ' + esc(err.message || 'Unknown error') + '</div>';
            });
        },

        searchGeneration: 0,

        renderSearchResults: function (results) {
            var content = view.querySelector('#aw-content');
            if (!results || results.length === 0) {
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">\uD83D\uDD0D</div>No results found. Try different keywords.</div>';
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
                if (source === 'hianime') {
                    html += '<span class="aw-browse-source-badge aw-badge-hianime"><img src="' + siteLogoUrl(source) + '" onerror="this.parentNode.style.display=\'none\'" /></span>';
                } else {
                    var badgeCls = 'aw-browse-source-badge' + (source === 'aniworld' ? ' aw-badge-aniworld' : '');
                    html += '<img class="' + badgeCls + '" src="' + siteLogoUrl(source) + '" onerror="this.style.display=\'none\'" />';
                }
                html += '<div class="aw-browse-info">';
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
            this.existingFoldersLoaded = false;
            this.episodeOffset = 0;

            // If called from browse tab, switch to search tab to show the detail view
            var browseTab = view.querySelector('#browseTab');
            if (browseTab && browseTab.style.display !== 'none') {
                this.browseReturnTo = true;
                view.querySelectorAll('.aw-tab').forEach(function (t) { t.classList.remove('active'); });
                view.querySelector('[data-tab="search"]').classList.add('active');
                view.querySelector('#searchTab').style.display = '';
                view.querySelector('#browseTab').style.display = 'none';
            }

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
                content.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">\u274C</div>Failed to load series: ' + esc(err.message || 'Unknown error') + '</div>';
            });
        },

        renderSeries: function (series, seriesUrl) {
            var content = view.querySelector('#aw-content');
            var source = this.currentSeriesSource || 'aniworld';
            var html = '<button class="aw-btn aw-btn-secondary aw-back" onclick="window.AW.goBack()">\u2190 Back to Results</button>';

            if (source === 'sto' && series.Genres && series.Genres.some(function (g) { return g.toLowerCase() === 'anime'; })) {
                html += '<div class="aw-warning">\u26A0\uFE0F For anime, using AniWorld as source is recommended.</div>';
            }

            if (source === 'hianime') {
                html += '<div class="aw-warning">\u26A0\uFE0F HiAnime is not recommended. Only use it for English dub or if AniWorld does not have the anime you are looking for. Each HiAnime entry maps to Season 01, so search for the specific season you want.</div>';
            }

            html += '<div class="aw-series">';
            if (series.CoverImageUrl) {
                html += '<img class="aw-cover" src="' + esc(series.CoverImageUrl) + '" alt="Cover" onerror="this.style.display=\'none\'" />';
            }
            html += '<div class="aw-meta">';
            var logoClass = source === 'hianime' ? 'aw-source-logo-wide' : 'aw-source-logo';
            html += '<h2><img class="' + logoClass + '" src="' + siteLogoUrl(source) + '" onerror="this.style.display=\'none\'" style="height:1.3em"> ' + esc(series.Title) + '</h2>';

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
                html += '<div class="aw-seasons">';
                series.Seasons.forEach(function (season, idx) {
                    var cls = idx === 0 ? ' active' : '';
                    var seasonLabel = source === 'hianime' ? 'Season' : 'Season ' + season.Number;
                    html += '<button class="aw-season' + cls + '" data-url="' + esc(season.Url) + '" onclick="window.AW.loadSeason(\'' + encodeURIComponent(season.Url) + '\', this)">' + seasonLabel + '</button>';
                });
                if (series.HasMovies) {
                    var movieUrl = seriesUrl + '/filme';
                    html += '<button class="aw-season" data-url="' + esc(movieUrl) + '" onclick="window.AW.loadSeason(\'' + encodeURIComponent(movieUrl) + '\', this)">\uD83C\uDFAC Movies</button>';
                }
                html += '</div>';
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
        loadSeason: function (encodedUrl, btn) {
            if (btn) {
                view.querySelectorAll('.aw-season').forEach(function (b) { b.classList.remove('active'); });
                btn.classList.add('active');
            }

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
                epContainer.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">\uD83D\uDCED</div>No episodes found.</div>';
                if (barContainer) barContainer.innerHTML = '';
                return;
            }

            if (barContainer) {
                var bar = '<div class="aw-season-actions">';
                bar += '<span class="aw-ep-count">' + episodes.length + ' episode' + (episodes.length === 1 ? '' : 's') + '</span>';
                bar += '<select id="aw-season-lang" class="aw-lang-select" title="Language for downloads">';
                bar += getLangOptionsHtml(source);
                bar += '</select>';
                bar += '<button class="aw-btn aw-btn-success aw-btn-sm" onclick="window.AW.downloadSeason(\'' + encodeURIComponent(seasonUrl) + '\')">\u2B07\uFE0F Download Season</button>';
                if (AW.currentSeriesUrl && source !== 'hianime') {
                    bar += '<button class="aw-btn aw-btn-all-seasons aw-btn-sm" onclick="window.AW.downloadAllSeasons(\'' + encodeURIComponent(AW.currentSeriesUrl) + '\')">\u2B07\uFE0F Download All Seasons</button>';
                }
                bar += '</div>';

                // HiAnime: "Download to existing title" controls
                if (source === 'hianime') {
                    bar += '<div class="aw-custom-target">';
                    bar += '<label class="aw-custom-target-toggle"><input type="checkbox" id="aw-custom-target-cb" onchange="window.AW.toggleExistingTarget()"> Download to existing title</label>';
                    bar += '<div id="aw-custom-target-notice" class="aw-custom-notice">Will be Season 01</div>';
                    bar += '<div id="aw-custom-target-fields" style="display:none">';
                    bar += '<select id="aw-custom-folder" onchange="window.AW.checkSeasonExists()"><option value="">-- Select folder --</option></select>';
                    bar += '<label class="aw-custom-season-label">Season <input type="number" id="aw-custom-season" min="0" value="1" onchange="window.AW.checkSeasonExists()" oninput="window.AW.checkSeasonExists()"></label>';
                    bar += '</div>';
                    bar += '<div id="aw-custom-target-append" class="aw-custom-append" style="display:none"></div>';
                    bar += '</div>';
                }

                barContainer.innerHTML = bar;

                // Enforce "Only English Dub" setting for HiAnime
                if (source === 'hianime' && AW.hiAnimeOnlyDub) {
                    var langSel = view.querySelector('#aw-season-lang');
                    if (langSel) {
                        langSel.value = 'dub';
                        langSel.disabled = true;
                        langSel.title = 'Locked to English Dub by admin setting';
                    }
                }

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
                html += '<button class="aw-btn aw-btn-primary aw-btn-sm" onclick="window.AW.downloadEpisode(\'' + encodeURIComponent(ep.Url) + '\', ' + ep.Number + ')">\u2B07\uFE0F Download</button>';
                html += '<button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.toggleProviders(\'' + encodeURIComponent(ep.Url) + '\', \'' + epId + '\', ' + ep.Number + ')">Providers</button>';
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
                url: ApiClient.getUrl('AniWorld/IsDownloaded', { url: url }),
                type: 'GET',
                dataType: 'json'
            }).then(function (result) {
                if (result && result.downloaded && result.languages && result.languages.length > 0) {
                    var badge = view.querySelector('#' + epId + '-dl');
                    if (badge) {
                        var html = '\u2713 ';
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
        toggleProviders: function (encodedUrl, epId, episodeNumber) {
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
                    if (source === 'hianime' && AW.hiAnimeOnlyDub && langKey !== 'dub') continue;
                    if (source === 'aniworld' && AW.aniWorldOnlyGerman && langKey === '2') continue;

                    hasAny = true;
                    html += '<div class="aw-lang-group">';
                    html += '<div class="aw-lang-label">' + langLabelHtml(langKey, source) + '</div>';
                    html += '<div class="aw-provider-btns">';
                    var providers = details.ProvidersByLanguage[langKey];
                    for (var prov in providers) {
                        html += '<button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.downloadWithOptions(\'' + encodeURIComponent(url) + '\', \'' + escJs(langKey) + '\', \'' + escJs(prov) + '\', ' + episodeNumber + ')">' + esc(prov) + '</button>';
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

        // ── Custom Target (HiAnime) ──
        toggleExistingTarget: function () {
            var cb = view.querySelector('#aw-custom-target-cb');
            var fields = view.querySelector('#aw-custom-target-fields');
            var notice = view.querySelector('#aw-custom-target-notice');
            var append = view.querySelector('#aw-custom-target-append');
            if (!cb || !fields || !notice) return;

            if (cb.checked) {
                fields.style.display = '';
                notice.style.display = 'none';
                if (append) append.style.display = 'none';
                this.episodeOffset = 0;
                this._renumberEpisodes(0);
                if (!this.existingFoldersLoaded) {
                    this.loadExistingFolders();
                }
                this.checkSeasonExists();
            } else {
                fields.style.display = 'none';
                notice.style.display = '';
                if (append) append.style.display = 'none';
                this.episodeOffset = 0;
                this._renumberEpisodes(0);
            }
        },

        loadExistingFolders: function () {
            var self = this;
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/ExistingFolders', { source: 'hianime' }),
                type: 'GET',
                dataType: 'json'
            }).then(function (folders) {
                var select = view.querySelector('#aw-custom-folder');
                if (!select) return;
                select.innerHTML = '<option value="">-- Select folder --</option>';
                (folders || []).forEach(function (name) {
                    var opt = document.createElement('option');
                    opt.value = name;
                    opt.textContent = name;
                    select.appendChild(opt);
                });
                self.existingFoldersLoaded = true;
            }).catch(function () {
                var select = view.querySelector('#aw-custom-folder');
                if (select) select.innerHTML = '<option value="">Failed to load folders</option>';
            });
        },

        checkSeasonExists: function () {
            var folder = (view.querySelector('#aw-custom-folder') || {}).value;
            var seasonInput = view.querySelector('#aw-custom-season');
            var season = seasonInput ? parseInt(seasonInput.value, 10) : NaN;
            var append = view.querySelector('#aw-custom-target-append');

            if (!folder || isNaN(season) || season < 0) {
                if (append) append.style.display = 'none';
                this.episodeOffset = 0;
                this._renumberEpisodes(0);
                return;
            }

            var self = this;
            ApiClient.fetch({
                url: ApiClient.getUrl('AniWorld/SeasonInfo', { folder: folder, season: season, source: 'hianime' }),
                type: 'GET',
                dataType: 'json'
            }).then(function (info) {
                if (info && info.exists && info.highestEpisode > 0) {
                    self.episodeOffset = info.highestEpisode;
                    if (append) {
                        var sLabel = 'Season ' + (season < 10 ? '0' : '') + season;
                        append.textContent = sLabel + ' already has ' + info.highestEpisode + ' episode(s) \u2014 new episodes will start at E' + (info.highestEpisode + 1);
                        append.style.display = '';
                    }
                    self._renumberEpisodes(info.highestEpisode);
                } else {
                    self.episodeOffset = 0;
                    if (append) append.style.display = 'none';
                    self._renumberEpisodes(0);
                }
            }).catch(function () {
                self.episodeOffset = 0;
                if (append) append.style.display = 'none';
                self._renumberEpisodes(0);
            });
        },

        _renumberEpisodes: function (offset) {
            var nums = view.querySelectorAll('.aw-ep-num');
            for (var i = 0; i < nums.length; i++) {
                var el = nums[i];
                // Store original number on first call
                if (!el.dataset.origNum) {
                    el.dataset.origNum = el.textContent.trim();
                }
                var orig = parseInt(el.dataset.origNum, 10);
                if (!isNaN(orig)) {
                    el.textContent = orig + offset;
                }
            }
        },

        _getHiAnimeCustomTarget: function () {
            var cb = view.querySelector('#aw-custom-target-cb');
            if (!cb || !cb.checked) return null;

            var folder = (view.querySelector('#aw-custom-folder') || {}).value;
            var seasonInput = view.querySelector('#aw-custom-season');
            var season = seasonInput ? parseInt(seasonInput.value, 10) : NaN;

            if (!folder) {
                Dashboard.alert('Please select a target folder.');
                return false;
            }
            if (isNaN(season) || season < 0) {
                Dashboard.alert('Please enter a valid season number (>= 0).');
                return false;
            }

            return { folder: folder, season: season, offset: this.episodeOffset };
        },

        // ── Downloads ──
        downloadEpisode: function (encodedUrl, episodeNumber) {
            var url = decodeURIComponent(encodedUrl);
            var langSelect = view.querySelector('#aw-season-lang');
            var lang = (langSelect && langSelect.value) ? langSelect.value : null;
            var customTarget = this._getHiAnimeCustomTarget();
            if (customTarget === false) return;
            this._startDownload(url, lang, null, episodeNumber, customTarget);
        },

        downloadWithOptions: function (encodedUrl, langKey, provider, episodeNumber) {
            var url = decodeURIComponent(encodedUrl);
            var customTarget = this._getHiAnimeCustomTarget();
            if (customTarget === false) return;
            this._startDownload(url, langKey, provider, episodeNumber, customTarget);
        },

        downloadSeason: function (encodedSeasonUrl) {
            var seasonUrl = decodeURIComponent(encodedSeasonUrl);
            var customTarget = this._getHiAnimeCustomTarget();
            if (customTarget === false) return;

            var body = {
                SeasonUrl: seasonUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };

            if (customTarget) {
                body.CustomFolder = customTarget.folder;
                body.CustomSeason = customTarget.season;
                body.EpisodeOffset = customTarget.offset;
            }

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
            var seriesUrl = decodeURIComponent(encodedSeriesUrl);
            var customTarget = this._getHiAnimeCustomTarget();
            if (customTarget === false) return;

            var body = {
                SeriesUrl: seriesUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };

            if (customTarget) {
                body.CustomFolder = customTarget.folder;
                body.CustomSeason = customTarget.season;
                body.EpisodeOffset = customTarget.offset;
            }

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

        _startDownload: function (episodeUrl, langKey, provider, episodeNumber, customTarget) {
            var body = {
                EpisodeUrl: episodeUrl,
                SeriesTitle: this.currentSeriesTitle,
                Source: this.currentSeriesSource || 'aniworld'
            };
            if (langKey) body.LanguageKey = langKey;
            if (provider) body.Provider = provider;
            if (episodeNumber != null) body.EpisodeNumber = episodeNumber;
            if (customTarget) {
                body.CustomFolder = customTarget.folder;
                body.CustomSeason = customTarget.season;
                body.EpisodeOffset = customTarget.offset;
            }

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
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">\uD83D\uDCED</div>No active downloads.<br>Search for anime and start downloading!</div>';
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
                html += '<div class="aw-dl-actions"><button class="aw-btn aw-btn-secondary aw-btn-sm" onclick="window.AW.clearCompleted()">\uD83E\uDDF9 Clear Completed</button></div>';
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
                html += '<small>' + esc(dl.Provider) + ' \u00B7 ' + esc(dl.Status);
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
                    html += '<div class="aw-dl-retry-info">\u23F3 ' + esc(dl.Error) + '</div>';
                }
                html += '</div>';

                html += '<div class="aw-dl-progress"><div class="aw-dl-bar" style="width:' + dl.Progress + '%"></div></div>';
                html += '<span class="aw-dl-pct">' + dl.Progress + '%</span>';
                html += '<span class="aw-status ' + statusCls + '">' + esc(dl.Status) + '</span>';

                html += '<div class="aw-dl-btns">';
                if (isActive) {
                    html += '<button class="aw-btn aw-btn-danger aw-btn-sm" onclick="window.AW.cancelDownload(\'' + dl.Id + '\')" title="Cancel">\u2715</button>';
                }
                if (isFailed) {
                    html += '<button class="aw-btn aw-btn-warning aw-btn-sm" onclick="window.AW.retryDownload(\'' + dl.Id + '\')" title="Retry">\uD83D\uDD04</button>';
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
            html += '<div class="aw-stat"><div class="aw-stat-value">' + stats.TotalDownloads + '</div><div class="aw-stat-label">Total Downloads</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value green">' + stats.Completed + '</div><div class="aw-stat-label">Completed</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value red">' + stats.Failed + '</div><div class="aw-stat-label">Failed</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value">' + formatSize(stats.TotalBytes) + '</div><div class="aw-stat-label">Total Size</div></div>';
            html += '<div class="aw-stat"><div class="aw-stat-value orange">' + stats.UniqueSeriesCount + '</div><div class="aw-stat-label">Series</div></div>';
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
            html += '<input type="text" id="aw-hist-series" placeholder="Filter by series..." style="padding:0.4em 0.7em;background:rgba(255,255,255,0.06);border:1px solid rgba(255,255,255,0.12);border-radius:6px;color:inherit;font-size:0.85em;min-width:200px;" />';
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
                container.innerHTML = '<div class="aw-empty"><div class="aw-empty-icon">\uD83D\uDCED</div>No download history yet.<br>Downloaded episodes will appear here.</div>';
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
            this.existingFoldersLoaded = false;
            this.episodeOffset = 0;
            if (this.browseReturnTo) {
                this.switchTab('browse');
                this.browseReturnTo = null;
            } else if (this.lastSearchResults) {
                this.renderSearchResults(this.lastSearchResults);
            } else if (this.lastSearchQuery) {
                view.querySelector('#aw-search-input').value = this.lastSearchQuery;
                this.search();
            } else {
                view.querySelector('#aw-content').innerHTML = '';
            }
        }
    };

    // Expose globally for onclick handlers in dynamic HTML
    window.AW = AW;

    // Load HiAnime "only dub" setting from server
    ApiClient.fetch({
        url: ApiClient.getUrl('AniWorld/EnabledSources'),
        type: 'GET',
        dataType: 'json'
    }).then(function (sources) {
        AW.hiAnimeOnlyDub = sources.hiAnimeOnlyDub === true;
        AW.aniWorldOnlyGerman = sources.aniWorldOnlyGerman === true;
    }).catch(function () { /* ignore */ });

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
