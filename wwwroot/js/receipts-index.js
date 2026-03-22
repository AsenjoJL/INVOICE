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

    const shouldRestore = sessionStorage.getItem(RESTORE_KEY) === '1';
    if (!shouldRestore) {
        return;
    }

    const highlightId = sessionStorage.getItem(HIGHLIGHT_KEY);
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
        const y = parseInt(sessionStorage.getItem(SCROLL_KEY) || '0', 10);
        if (!Number.isNaN(y) && y > 0) {
            window.requestAnimationFrame(() => window.scrollTo(0, y));
        }
    }

    sessionStorage.removeItem(RESTORE_KEY);
    sessionStorage.removeItem(SCROLL_KEY);
    sessionStorage.removeItem(HIGHLIGHT_KEY);
})();
