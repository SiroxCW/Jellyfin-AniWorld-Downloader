export default function (view, params) {
    var pluginId = 'e93d1d02-df60-4545-ae3c-7bb87dff024c';

    function loadConfig() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            // General
            view.querySelector('#txtMaxDownloads').value = config.MaxConcurrentDownloads || 2;
            view.querySelector('#txtMaxRetries').value = config.MaxRetries != null ? config.MaxRetries : 3;
            view.querySelector('#chkAutoScan').checked = config.AutoScanLibrary !== false;
            view.querySelector('#chkNonAdminAccess').checked = config.EnableNonAdminAccess === true;
            view.querySelector('#chkMaintenanceMode').checked = config.MaintenanceMode === true;
            view.querySelector('#txtMaintenanceMessage').value = config.MaintenanceMessage || '';
            view.querySelector('#txtProxyUrl').value = config.ProxyUrl || '';
            view.querySelector('#txtMoviePath').value = config.MovieDownloadPath || '';
            view.querySelector('#txtLanguageFallbackOrder').value = config.LanguageFallbackOrder || '1';

            // AniWorld
            var aw = config.AniWorldConfig || {};
            var awFallback = aw.DownloadPath || config.DownloadPath || '';
            view.querySelector('#chkAniWorldEnabled').checked = aw.Enabled !== false;
            view.querySelector('#txtAniWorldPath1').value = aw.DownloadPath1 || awFallback;
            view.querySelector('#txtAniWorldPath2').value = aw.DownloadPath2 || '';
            view.querySelector('#txtAniWorldPath3').value = aw.DownloadPath3 || '';
            view.querySelector('#selAniWorldLanguage').value = aw.PreferredLanguage || config.PreferredLanguage || '1';
            view.querySelector('#selAniWorldProvider').value = aw.PreferredProvider || config.PreferredProvider || 'VOE';
            view.querySelector('#selAniWorldFallback').value = aw.FallbackProvider || config.FallbackProvider || '';
            view.querySelector('#chkAniWorldOnlyGerman').checked = aw.OnlyGermanLanguages === true;

            // s.to
            var sto = config.StoConfig || {};
            var stoFallback = sto.DownloadPath || '';
            view.querySelector('#chkStoEnabled').checked = sto.Enabled === true;
            view.querySelector('#txtStoBaseUrl').value = config.StoBaseUrl || '';
            view.querySelector('#txtStoPath1').value = sto.DownloadPath1 || stoFallback;
            view.querySelector('#txtStoPath2').value = sto.DownloadPath2 || '';
            view.querySelector('#selStoLanguage').value = sto.PreferredLanguage || '1';
            view.querySelector('#selStoProvider').value = sto.PreferredProvider || 'VOE';
            view.querySelector('#selStoFallback').value = sto.FallbackProvider || '';

            // filmo.to
            var filmo = config.FilmoConfig || {};
            var filmoFallback = filmo.DownloadPath || '';
            view.querySelector('#chkFilmoEnabled').checked = filmo.Enabled === true;
            view.querySelector('#txtFilmoPath1').value = filmo.DownloadPath1 || filmoFallback;
            view.querySelector('#txtFilmoPath2').value = filmo.DownloadPath2 || '';
            view.querySelector('#selFilmoLanguage').value = filmo.PreferredLanguage || '1';
            view.querySelector('#selFilmoProvider').value = filmo.PreferredProvider || 'VOE';
            view.querySelector('#selFilmoFallback').value = filmo.FallbackProvider || '';

            // FilmPalast
            var filmPalast = config.FilmPalastConfig || {};
            var filmPalastFallback = filmPalast.DownloadPath || '';
            view.querySelector('#chkFilmPalastEnabled').checked = filmPalast.Enabled === true;
            view.querySelector('#txtFilmPalastPath1').value = filmPalast.DownloadPath1 || filmPalastFallback;
            view.querySelector('#txtFilmPalastPath2').value = filmPalast.DownloadPath2 || '';
            view.querySelector('#selFilmPalastLanguage').value = filmPalast.PreferredLanguage || '1';
            view.querySelector('#selFilmPalastProvider').value = filmPalast.PreferredProvider || 'VOE';
            view.querySelector('#selFilmPalastFallback').value = filmPalast.FallbackProvider || '';

            // MegaKino
            var megaKino = config.MegaKinoConfig || {};
            var megaKinoFallback = megaKino.DownloadPath || '';
            view.querySelector('#chkMegaKinoEnabled').checked = megaKino.Enabled === true;
            view.querySelector('#txtMegaKinoPath1').value = megaKino.DownloadPath1 || megaKinoFallback;
            view.querySelector('#txtMegaKinoPath2').value = megaKino.DownloadPath2 || '';
            view.querySelector('#selMegaKinoLanguage').value = megaKino.PreferredLanguage || '1';
            view.querySelector('#selMegaKinoProvider').value = megaKino.PreferredProvider || 'VOE';
            view.querySelector('#selMegaKinoFallback').value = megaKino.FallbackProvider || '';

            // Moflix
            var moflix = config.MoflixConfig || {};
            var moflixFallback = moflix.DownloadPath || '';
            view.querySelector('#chkMoflixEnabled').checked = moflix.Enabled === true;
            view.querySelector('#txtMoflixPath1').value = moflix.DownloadPath1 || moflixFallback;
            view.querySelector('#txtMoflixPath2').value = moflix.DownloadPath2 || '';
            view.querySelector('#selMoflixLanguage').value = moflix.PreferredLanguage || '1';
            view.querySelector('#selMoflixProvider').value = moflix.PreferredProvider || 'MoflixClick';
            view.querySelector('#selMoflixFallback').value = moflix.FallbackProvider || '';

            Dashboard.hideLoadingMsg();
        });
    }

    function saveConfig() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            // General
            config.MaxConcurrentDownloads = parseInt(view.querySelector('#txtMaxDownloads').value, 10) || 2;
            config.MaxRetries = parseInt(view.querySelector('#txtMaxRetries').value, 10) || 0;
            config.AutoScanLibrary = view.querySelector('#chkAutoScan').checked;
            config.EnableNonAdminAccess = view.querySelector('#chkNonAdminAccess').checked;
            config.MaintenanceMode = view.querySelector('#chkMaintenanceMode').checked;
            config.MaintenanceMessage = view.querySelector('#txtMaintenanceMessage').value.trim();
            config.ProxyUrl = view.querySelector('#txtProxyUrl').value.trim();
            config.MovieDownloadPath = view.querySelector('#txtMoviePath').value.trim();
            config.LanguageFallbackOrder = view.querySelector('#txtLanguageFallbackOrder').value.trim() || '1';

            // AniWorld
            if (!config.AniWorldConfig) config.AniWorldConfig = {};
            config.AniWorldConfig.Enabled = view.querySelector('#chkAniWorldEnabled').checked;
            config.AniWorldConfig.DownloadPath1 = view.querySelector('#txtAniWorldPath1').value.trim();
            config.AniWorldConfig.DownloadPath2 = view.querySelector('#txtAniWorldPath2').value.trim();
            config.AniWorldConfig.DownloadPath3 = view.querySelector('#txtAniWorldPath3').value.trim();
            config.AniWorldConfig.DownloadPath = config.AniWorldConfig.DownloadPath1;
            config.AniWorldConfig.PreferredLanguage = view.querySelector('#selAniWorldLanguage').value;
            config.AniWorldConfig.PreferredProvider = view.querySelector('#selAniWorldProvider').value;
            config.AniWorldConfig.FallbackProvider = view.querySelector('#selAniWorldFallback').value;
            config.AniWorldConfig.OnlyGermanLanguages = view.querySelector('#chkAniWorldOnlyGerman').checked;

            // Keep legacy flat fields in sync for backward compat
            config.DownloadPath = config.AniWorldConfig.DownloadPath;
            config.PreferredLanguage = config.AniWorldConfig.PreferredLanguage;
            config.PreferredProvider = config.AniWorldConfig.PreferredProvider;
            config.FallbackProvider = config.AniWorldConfig.FallbackProvider;

            // s.to
            if (!config.StoConfig) config.StoConfig = {};
            config.StoConfig.Enabled = view.querySelector('#chkStoEnabled').checked;
            config.StoBaseUrl = view.querySelector('#txtStoBaseUrl').value.trim();
            config.StoConfig.DownloadPath1 = view.querySelector('#txtStoPath1').value.trim();
            config.StoConfig.DownloadPath2 = view.querySelector('#txtStoPath2').value.trim();
            config.StoConfig.DownloadPath = config.StoConfig.DownloadPath1;
            config.StoConfig.PreferredLanguage = view.querySelector('#selStoLanguage').value;
            config.StoConfig.PreferredProvider = view.querySelector('#selStoProvider').value;
            config.StoConfig.FallbackProvider = view.querySelector('#selStoFallback').value;

            // filmo.to
            if (!config.FilmoConfig) config.FilmoConfig = {};
            config.FilmoConfig.Enabled = view.querySelector('#chkFilmoEnabled').checked;
            config.FilmoConfig.DownloadPath1 = view.querySelector('#txtFilmoPath1').value.trim();
            config.FilmoConfig.DownloadPath2 = view.querySelector('#txtFilmoPath2').value.trim();
            config.FilmoConfig.DownloadPath = config.FilmoConfig.DownloadPath1;
            config.FilmoConfig.PreferredLanguage = view.querySelector('#selFilmoLanguage').value;
            config.FilmoConfig.PreferredProvider = view.querySelector('#selFilmoProvider').value;
            config.FilmoConfig.FallbackProvider = view.querySelector('#selFilmoFallback').value;

            // FilmPalast
            if (!config.FilmPalastConfig) config.FilmPalastConfig = {};
            config.FilmPalastConfig.Enabled = view.querySelector('#chkFilmPalastEnabled').checked;
            config.FilmPalastConfig.DownloadPath1 = view.querySelector('#txtFilmPalastPath1').value.trim();
            config.FilmPalastConfig.DownloadPath2 = view.querySelector('#txtFilmPalastPath2').value.trim();
            config.FilmPalastConfig.DownloadPath = config.FilmPalastConfig.DownloadPath1;
            config.FilmPalastConfig.PreferredLanguage = view.querySelector('#selFilmPalastLanguage').value;
            config.FilmPalastConfig.PreferredProvider = view.querySelector('#selFilmPalastProvider').value;
            config.FilmPalastConfig.FallbackProvider = view.querySelector('#selFilmPalastFallback').value;

            // MegaKino
            if (!config.MegaKinoConfig) config.MegaKinoConfig = {};
            config.MegaKinoConfig.Enabled = view.querySelector('#chkMegaKinoEnabled').checked;
            config.MegaKinoConfig.DownloadPath1 = view.querySelector('#txtMegaKinoPath1').value.trim();
            config.MegaKinoConfig.DownloadPath2 = view.querySelector('#txtMegaKinoPath2').value.trim();
            config.MegaKinoConfig.DownloadPath = config.MegaKinoConfig.DownloadPath1;
            config.MegaKinoConfig.PreferredLanguage = view.querySelector('#selMegaKinoLanguage').value;
            config.MegaKinoConfig.PreferredProvider = view.querySelector('#selMegaKinoProvider').value;
            config.MegaKinoConfig.FallbackProvider = view.querySelector('#selMegaKinoFallback').value;

            // Moflix
            if (!config.MoflixConfig) config.MoflixConfig = {};
            config.MoflixConfig.Enabled = view.querySelector('#chkMoflixEnabled').checked;
            config.MoflixConfig.DownloadPath1 = view.querySelector('#txtMoflixPath1').value.trim();
            config.MoflixConfig.DownloadPath2 = view.querySelector('#txtMoflixPath2').value.trim();
            config.MoflixConfig.DownloadPath = config.MoflixConfig.DownloadPath1;
            config.MoflixConfig.PreferredLanguage = view.querySelector('#selMoflixLanguage').value;
            config.MoflixConfig.PreferredProvider = view.querySelector('#selMoflixProvider').value;
            config.MoflixConfig.FallbackProvider = view.querySelector('#selMoflixFallback').value;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    }

    view.addEventListener('viewshow', function () {
        loadConfig();
    });

    view.querySelector('#AniWorldConfigForm').addEventListener('submit', function (e) {
        e.preventDefault();
        saveConfig();
        return false;
    });
}
