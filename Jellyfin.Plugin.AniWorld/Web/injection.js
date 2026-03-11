(function () {
    'use strict';

    var MENU_ID = 'aw-sidebar-entry';
    var MODAL_ID = 'aw-modal-overlay';

    function getApiClient() {
        if (typeof ApiClient !== 'undefined') return ApiClient;
        if (typeof window.ApiClient !== 'undefined') return window.ApiClient;
        return null;
    }

    function waitForApiClient(callback) {
        var api = getApiClient();
        if (api) {
            callback(api);
            return;
        }
        var attempts = 0;
        var interval = setInterval(function () {
            api = getApiClient();
            if (api || attempts > 100) {
                clearInterval(interval);
                if (api) callback(api);
            }
            attempts++;
        }, 200);
    }

    function injectSidebarEntry() {
        if (document.getElementById(MENU_ID)) return;

        var sidebar = document.querySelector('.mainDrawer-scrollContainer');
        if (!sidebar) return;

        var customSection = sidebar.querySelector('.customMenuOptions');
        var adminSection = sidebar.querySelector('.adminMenuOptions');

        var entry = document.createElement('a');
        entry.id = MENU_ID;
        entry.className = 'navMenuOption lnkMediaFolder';
        entry.href = '#';
        entry.setAttribute('data-itemid', 'aniworld');
        entry.innerHTML = '<span class="material-icons navMenuOptionIcon download" aria-hidden="true"></span>' +
            '<span class="navMenuOptionText">AniWorld Downloader</span>';

        entry.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();

            var backdrop = document.querySelector('.mainDrawer-backdrop');
            if (backdrop) backdrop.click();

            showModal();
        });

        if (customSection) {
            customSection.appendChild(entry);
        } else if (adminSection) {
            adminSection.parentNode.insertBefore(entry, adminSection);
        } else {
            sidebar.appendChild(entry);
        }
    }

    function showModal() {
        // Always remove old modal to avoid stale window.AW references
        var existing = document.getElementById(MODAL_ID);
        if (existing) {
            existing.remove();
        }

        // Create full-screen modal
        var overlay = document.createElement('div');
        overlay.id = MODAL_ID;
        overlay.style.cssText = 'position:fixed;top:0;left:0;right:0;bottom:0;z-index:999;display:flex;flex-direction:column;background:#181818;';

        // Header
        var header = document.createElement('div');
        header.style.cssText = 'display:flex;align-items:center;justify-content:space-between;padding:0.5em 1em;background:#101010;border-bottom:1px solid rgba(255,255,255,0.08);flex-shrink:0;';

        var title = document.createElement('span');
        title.textContent = 'AniWorld Downloader';
        title.style.cssText = 'font-size:1.1em;font-weight:600;color:#fff;';

        var closeBtn = document.createElement('button');
        closeBtn.innerHTML = '<span class="material-icons" style="font-size:1.5em;">close</span>';
        closeBtn.title = 'Close';
        closeBtn.style.cssText = 'background:none;border:none;color:#fff;cursor:pointer;padding:0.3em;border-radius:50%;display:flex;align-items:center;opacity:0.7;';
        closeBtn.addEventListener('mouseenter', function () { this.style.opacity = '1'; });
        closeBtn.addEventListener('mouseleave', function () { this.style.opacity = '0.7'; });
        closeBtn.addEventListener('click', hideModal);

        header.appendChild(title);
        header.appendChild(closeBtn);

        // Scrollable content
        var content = document.createElement('div');
        content.id = 'aw-modal-content';
        content.style.cssText = 'flex:1;overflow-y:auto;background:#181818;color:#eee;';

        // Loading spinner
        content.innerHTML = '<div style="display:flex;align-items:center;justify-content:center;padding:3em;opacity:0.6;">' +
            '<span style="display:inline-block;width:1.2em;height:1.2em;border:2px solid rgba(255,255,255,0.2);border-top-color:#00a4dc;border-radius:50%;animation:aw-inj-spin 0.8s linear infinite;margin-right:0.8em;"></span>' +
            'Loading...</div><style>@keyframes aw-inj-spin{to{transform:rotate(360deg);}}</style>';

        overlay.appendChild(header);
        overlay.appendChild(content);

        // Close on Escape
        document.addEventListener('keydown', function escHandler(e) {
            if (e.key === 'Escape') {
                hideModal();
            }
        });

        document.body.appendChild(overlay);

        loadPage(content);
    }

    function loadPage(content) {
        var api = getApiClient();
        if (!api) {
            content.innerHTML = '<div style="padding:2em;text-align:center;opacity:0.6;">API not available.</div>';
            return;
        }

        api.fetch({
            url: api.getUrl('AniWorld/Page'),
            type: 'GET',
            dataType: 'text'
        }).then(function (html) {
            // Parse HTML and extract the page content
            var parser = new DOMParser();
            var doc = parser.parseFromString(html, 'text/html');
            var pageDiv = doc.querySelector('[data-role="page"]');

            if (pageDiv) {
                content.innerHTML = pageDiv.innerHTML;
            } else {
                content.innerHTML = html;
            }

            // Load the script with a cache-busting parameter so import() always creates a fresh module
            var scriptUrl = api.getUrl('AniWorld/PageScript') + '?_t=' + Date.now();
            import(scriptUrl).then(function (module) {
                if (module.default && typeof module.default === 'function') {
                    module.default(content, { sidebar: true });
                }

                // Always hide settings button in sidebar view
                var settingsBtn = content.querySelector('#aw-settings-btn');
                if (settingsBtn) {
                    settingsBtn.style.display = 'none';
                }
            }).catch(function (err) {
                console.error('AniWorld: Failed to load page script:', err);
            });
        }).catch(function (err) {
            content.innerHTML = '<div style="padding:2em;text-align:center;opacity:0.6;">' +
                'Failed to load AniWorld Downloader. Please check your permissions.</div>';
            console.error('AniWorld: Failed to load page:', err);
        });
    }

    function hideModal() {
        var overlay = document.getElementById(MODAL_ID);
        if (overlay) {
            // Fire viewhide to clean up polling timers
            var content = overlay.querySelector('#aw-modal-content');
            if (content) {
                content.dispatchEvent(new Event('viewhide'));
            }
            overlay.style.display = 'none';
        }
    }

    function setupObserver() {
        var observer = new MutationObserver(function () {
            injectSidebarEntry();
        });
        observer.observe(document.body, { childList: true, subtree: true });
        injectSidebarEntry();
    }

    waitForApiClient(function () {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', setupObserver);
        } else {
            setupObserver();
        }
    });
})();
