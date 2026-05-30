(function () {
    'use strict';

    const SCROLL_KEY = 'receipts:index:scrollY';
    const RESTORE_KEY = 'receipts:index:restore';
    const HIGHLIGHT_KEY = 'receipts:index:highlightId';

    function rememberListPosition(receiptId) {
        sessionStorage.setItem(SCROLL_KEY, String(window.scrollY || 0));
        sessionStorage.setItem(RESTORE_KEY, '1');

        if (receiptId) {
            sessionStorage.setItem(HIGHLIGHT_KEY, String(receiptId));
        } else {
            sessionStorage.removeItem(HIGHLIGHT_KEY);
        }
    }

    document.querySelectorAll('.receipt-return-link').forEach(link => {
        link.addEventListener('click', () => {
            const receiptId = link.getAttribute('data-receipt-id');
            rememberListPosition(receiptId);
        });
    });

    document.querySelectorAll('.receipt-return-form').forEach(form => {
        form.addEventListener('submit', () => {
            const receiptId = form.getAttribute('data-receipt-id');
            rememberListPosition(receiptId);
        });
    });

    const url = new URL(window.location.href);
    const restoreScrollParam = parseInt(url.searchParams.get('restoreScrollY') || '0', 10);
    const highlightReceiptId = url.searchParams.get('highlightReceiptId');
    const shouldRestore = sessionStorage.getItem(RESTORE_KEY) === '1'
        || (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0)
        || !!highlightReceiptId;
    if (!shouldRestore) {
        return;
    }

    const highlightId = highlightReceiptId || sessionStorage.getItem(HIGHLIGHT_KEY);
    const highlightedRow = highlightId
        ? document.querySelector(`.receipt-row[data-id="${highlightId}"]`)
        : null;

    if (highlightedRow instanceof HTMLElement) {
        window.requestAnimationFrame(() => {
            highlightedRow.scrollIntoView({ behavior: 'smooth', block: 'center' });
            highlightedRow.classList.add('receipt-row-highlight');
            window.setTimeout(() => highlightedRow.classList.remove('receipt-row-highlight'), 2200);
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

    if ((Number.isFinite(restoreScrollParam) && restoreScrollParam > 0) || highlightReceiptId) {
        url.searchParams.delete('restoreScrollY');
        url.searchParams.delete('highlightReceiptId');
        window.history.replaceState({}, document.title, `${url.pathname}${url.search}`);
    }
})();
