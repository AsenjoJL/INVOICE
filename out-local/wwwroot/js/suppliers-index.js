(function () {
    'use strict';

    const SCROLL_KEY = 'suppliers:index:scrollY';
    const RESTORE_KEY = 'suppliers:index:restore';
    const HIGHLIGHT_KEY = 'suppliers:index:highlightId';

    function rememberListPosition(supplierId) {
        sessionStorage.setItem(SCROLL_KEY, String(window.scrollY || 0));
        sessionStorage.setItem(RESTORE_KEY, '1');

        if (supplierId) {
            sessionStorage.setItem(HIGHLIGHT_KEY, String(supplierId));
        } else {
            sessionStorage.removeItem(HIGHLIGHT_KEY);
        }
    }

    document.querySelectorAll('.supplier-return-link').forEach(link => {
        link.addEventListener('click', () => {
            rememberListPosition(link.getAttribute('data-supplier-id'));
        });
    });

    const url = new URL(window.location.href);
    const restoreScrollParam = parseInt(url.searchParams.get('restoreScrollY') || '0', 10);
    const highlightSupplierId = url.searchParams.get('highlightSupplierId');
    const shouldRestore = sessionStorage.getItem(RESTORE_KEY) === '1'
        || (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0)
        || !!highlightSupplierId;

    if (!shouldRestore) {
        return;
    }

    const highlightId = highlightSupplierId || sessionStorage.getItem(HIGHLIGHT_KEY);
    const highlightedRow = highlightId
        ? document.querySelector(`.supplier-row[data-id="${highlightId}"]`)
        : null;

    if (highlightedRow instanceof HTMLElement) {
        window.requestAnimationFrame(() => {
            highlightedRow.scrollIntoView({ behavior: 'smooth', block: 'center' });
            highlightedRow.classList.add('table-primary');
            window.setTimeout(() => highlightedRow.classList.remove('table-primary'), 2200);
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

    if ((Number.isFinite(restoreScrollParam) && restoreScrollParam > 0) || highlightSupplierId) {
        url.searchParams.delete('restoreScrollY');
        url.searchParams.delete('highlightSupplierId');
        window.history.replaceState({}, document.title, `${url.pathname}${url.search}`);
    }
})();
