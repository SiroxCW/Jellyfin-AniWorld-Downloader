export default function (view, params) {
    var pluginId = 'e93d1d02-df60-4545-ae3c-7bb87dff024c';

    function loadConfig() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('#txtDownloadPath').value = config.DownloadPath || '';
            view.querySelector('#selLanguage').value = config.PreferredLanguage || '1';
            view.querySelector('#selProvider').value = config.PreferredProvider || 'VOE';
            view.querySelector('#selFallbackProvider').value = config.FallbackProvider || '';
            view.querySelector('#txtMaxDownloads').value = config.MaxConcurrentDownloads || 2;
            view.querySelector('#txtMaxRetries').value = config.MaxRetries != null ? config.MaxRetries : 3;
            view.querySelector('#chkAutoScan').checked = config.AutoScanLibrary !== false;
            view.querySelector('#chkCheckForUpdates').checked = config.CheckForUpdates !== false;
            view.querySelector('#txtRepositoryUrl').value = config.RepositoryUrl || '';
            Dashboard.hideLoadingMsg();
        });
    }

    function saveConfig() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.DownloadPath = view.querySelector('#txtDownloadPath').value.trim();
            config.PreferredLanguage = view.querySelector('#selLanguage').value;
            config.PreferredProvider = view.querySelector('#selProvider').value;
            config.FallbackProvider = view.querySelector('#selFallbackProvider').value;
            config.MaxConcurrentDownloads = parseInt(view.querySelector('#txtMaxDownloads').value, 10) || 2;
            config.MaxRetries = parseInt(view.querySelector('#txtMaxRetries').value, 10) || 0;
            config.AutoScanLibrary = view.querySelector('#chkAutoScan').checked;
            config.CheckForUpdates = view.querySelector('#chkCheckForUpdates').checked;
            config.RepositoryUrl = view.querySelector('#txtRepositoryUrl').value.trim();

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
