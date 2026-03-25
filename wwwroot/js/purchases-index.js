(function () {
    'use strict';

    const SCROLL_KEY = 'purchases:index:scrollY';
    const RESTORE_KEY = 'purchases:index:restore';
    const HIGHLIGHT_KEY = 'purchases:index:highlightId';

    function rememberListPosition(purchaseId) {
        sessionStorage.setItem(SCROLL_KEY, String(window.scrollY || 0));
        sessionStorage.setItem(RESTORE_KEY, '1');

        if (purchaseId) {
            sessionStorage.setItem(HIGHLIGHT_KEY, String(purchaseId));
        } else {
            sessionStorage.removeItem(HIGHLIGHT_KEY);
        }
    }

    document.querySelectorAll('.purchase-return-link').forEach(link => {
        link.addEventListener('click', () => rememberListPosition(link.getAttribute('data-purchase-id')));
    });

    document.querySelectorAll('.purchase-return-form').forEach(form => {
        form.addEventListener('submit', () => {
            const purchaseId = form.getAttribute('data-purchase-id');
            rememberListPosition(purchaseId);

            const scrollInput = form.querySelector('.purchase-return-scroll');
            if (scrollInput instanceof HTMLInputElement) {
                scrollInput.value = String(window.scrollY || 0);
            }
        });
    });

    const url = new URL(window.location.href);
    const restoreScrollParam = parseInt(url.searchParams.get('restoreScrollY') || '0', 10);
    const highlightPurchaseId = url.searchParams.get('highlightPurchaseId');
    const shouldRestore = sessionStorage.getItem(RESTORE_KEY) === '1'
        || (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0)
        || !!highlightPurchaseId;

    if (!shouldRestore) {
        return;
    }

    const highlightId = highlightPurchaseId || sessionStorage.getItem(HIGHLIGHT_KEY);
    const highlightedRow = highlightId
        ? document.querySelector(`.purchase-row[data-id="${highlightId}"]`)
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

    if ((Number.isFinite(restoreScrollParam) && restoreScrollParam > 0) || highlightPurchaseId) {
        url.searchParams.delete('restoreScrollY');
        url.searchParams.delete('highlightPurchaseId');
        window.history.replaceState({}, document.title, `${url.pathname}${url.search}`);
    }
})();
