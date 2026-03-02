var AniWorldConfig = {
    pluginId: 'e93d1d02-df60-4545-ae3c-7bb87dff024c',

    load: function () {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(this.pluginId).then(function (config) {
            document.getElementById('txtDownloadPath').value = config.DownloadPath || '';
            document.getElementById('selLanguage').value = config.PreferredLanguage || '1';
            document.getElementById('selProvider').value = config.PreferredProvider || 'VOE';
            document.getElementById('txtMaxDownloads').value = config.MaxConcurrentDownloads || 2;
            document.getElementById('txtMaxRetries').value = config.MaxRetries != null ? config.MaxRetries : 3;
            document.getElementById('chkAutoScan').checked = config.AutoScanLibrary !== false;
            Dashboard.hideLoadingMsg();
        });
    },

    save: function () {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(this.pluginId).then(function (config) {
            config.DownloadPath = document.getElementById('txtDownloadPath').value.trim();
            config.PreferredLanguage = document.getElementById('selLanguage').value;
            config.PreferredProvider = document.getElementById('selProvider').value;
            config.MaxConcurrentDownloads = parseInt(document.getElementById('txtMaxDownloads').value, 10) || 2;
            config.MaxRetries = parseInt(document.getElementById('txtMaxRetries').value, 10) || 0;
            config.AutoScanLibrary = document.getElementById('chkAutoScan').checked;

            ApiClient.updatePluginConfiguration(AniWorldConfig.pluginId, config).then(function () {
                Dashboard.processPluginConfigurationUpdateResult();
            });
        });
    }
};

document.getElementById('AniWorldConfigPage').addEventListener('pageshow', function () {
    AniWorldConfig.load();
});

document.getElementById('AniWorldConfigForm').addEventListener('submit', function (e) {
    e.preventDefault();
    AniWorldConfig.save();
    return false;
});
