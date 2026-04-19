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

            // HiAnime — disabled: hianime.to has been shut down (March 2026)
            // Config elements are hidden but still present in the DOM for backward compat
            var hi = config.HiAnimeConfig || {};
            view.querySelector('#chkHiAnimeEnabled').checked = false;
            view.querySelector('#txtHiAnimePathSub').value = hi.DownloadPathSub || hi.DownloadPath || '';
            view.querySelector('#txtHiAnimePathDub').value = hi.DownloadPathDub || '';
            view.querySelector('#selHiAnimeLanguage').value = hi.PreferredLanguage || 'sub';
            view.querySelector('#chkHiAnimeOnlyDub').checked = hi.OnlyEnglishDub === true;

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

            // HiAnime — disabled: hianime.to has been shut down (March 2026)
            if (!config.HiAnimeConfig) config.HiAnimeConfig = {};
            config.HiAnimeConfig.Enabled = false;
            config.HiAnimeConfig.DownloadPathSub = view.querySelector('#txtHiAnimePathSub').value.trim();
            config.HiAnimeConfig.DownloadPathDub = view.querySelector('#txtHiAnimePathDub').value.trim();
            config.HiAnimeConfig.DownloadPath = config.HiAnimeConfig.DownloadPathSub;
            config.HiAnimeConfig.PreferredLanguage = view.querySelector('#selHiAnimeLanguage').value;
            config.HiAnimeConfig.OnlyEnglishDub = view.querySelector('#chkHiAnimeOnlyDub').checked;

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
