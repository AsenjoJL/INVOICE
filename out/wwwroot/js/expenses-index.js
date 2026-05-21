(function () {
    'use strict';

    const SCROLL_KEY = 'expenses:index:scrollY';
    const RESTORE_KEY = 'expenses:index:restore';

    document.querySelectorAll('.expense-return-link').forEach(link => {
        link.addEventListener('click', () => {
            sessionStorage.setItem(SCROLL_KEY, String(window.scrollY || 0));
            sessionStorage.setItem(RESTORE_KEY, '1');
        });
    });

    const url = new URL(window.location.href);
    const restoreScrollParam = parseInt(url.searchParams.get('restoreScrollY') || '0', 10);
    const shouldRestore = sessionStorage.getItem(RESTORE_KEY) === '1'
        || (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0);

    if (!shouldRestore) {
        return;
    }

    const y = parseInt(String(restoreScrollParam > 0 ? restoreScrollParam : (sessionStorage.getItem(SCROLL_KEY) || '0')), 10);
    if (!Number.isNaN(y) && y > 0) {
        window.requestAnimationFrame(() => window.scrollTo(0, y));
    }

    sessionStorage.removeItem(RESTORE_KEY);
    sessionStorage.removeItem(SCROLL_KEY);

    if (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0) {
        url.searchParams.delete('restoreScrollY');
        window.history.replaceState({}, document.title, `${url.pathname}${url.search}`);
    }
})();
