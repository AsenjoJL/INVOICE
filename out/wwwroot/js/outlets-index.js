(function () {
    'use strict';

    const SCROLL_KEY = 'outlets:index:scrollY';
    const RESTORE_KEY = 'outlets:index:restore';
    const HIGHLIGHT_KEY = 'outlets:index:highlightId';

    function rememberPosition(outletId) {
        sessionStorage.setItem(SCROLL_KEY, String(window.scrollY || 0));
        sessionStorage.setItem(RESTORE_KEY, '1');

        if (outletId) {
            sessionStorage.setItem(HIGHLIGHT_KEY, String(outletId));
        } else {
            sessionStorage.removeItem(HIGHLIGHT_KEY);
        }
    }

    document.querySelectorAll('.outlet-return-link').forEach(link => {
        link.addEventListener('click', () => {
            const outletId = link.getAttribute('data-outlet-id');
            rememberPosition(outletId);
        });
    });

    const url = new URL(window.location.href);
    const restoreScrollParam = parseInt(url.searchParams.get('restoreScrollY') || '0', 10);
    const highlightIdFromQuery = url.searchParams.get('highlightCustomerId');
    const shouldRestore = sessionStorage.getItem(RESTORE_KEY) === '1'
        || (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0)
        || !!highlightIdFromQuery;
    if (!shouldRestore) {
        return;
    }

    const highlightId = highlightIdFromQuery || sessionStorage.getItem(HIGHLIGHT_KEY);
    const highlightedRow = highlightId
        ? document.querySelector(`.outlet-row[data-id="${highlightId}"]`)
        : null;

    if (highlightedRow instanceof HTMLElement) {
        window.requestAnimationFrame(() => {
            highlightedRow.scrollIntoView({ behavior: 'smooth', block: 'center' });
            highlightedRow.classList.add('outlet-row-highlight');
            window.setTimeout(() => highlightedRow.classList.remove('outlet-row-highlight'), 2200);
        });
    } else {
        const y = parseInt(String(restoreScrollParam > 0 ? restoreScrollParam : (sessionStorage.getItem(SCROLL_KEY) || '0')), 10);
        if (!Number.isNaN(y) && y > 0) {
            window.requestAnimationFrame(() => window.scrollTo(0, y));
        }
    }

    sessionStorage.removeItem(RESTORE_KEY);
    sessionStorage.removeItem(SCROLL_KEY);
    sessionStorage.removeItem(HIGHLIGHT_KEY);

    if ((Number.isFinite(restoreScrollParam) && restoreScrollParam > 0) || highlightId) {
        url.searchParams.delete('restoreScrollY');
        url.searchParams.delete('highlightCustomerId');
        window.history.replaceState({}, document.title, `${url.pathname}${url.search}${url.hash}`);
    }
})();
