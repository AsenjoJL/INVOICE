(function () {
    'use strict';

    const SCROLL_KEY = 'laborers:index:scrollY';
    const RESTORE_KEY = 'laborers:index:restore';
    const HIGHLIGHT_KEY = 'laborers:index:highlightId';

    function rememberListPosition(laborerId) {
        sessionStorage.setItem(SCROLL_KEY, String(window.scrollY || 0));
        sessionStorage.setItem(RESTORE_KEY, '1');

        if (laborerId) {
            sessionStorage.setItem(HIGHLIGHT_KEY, String(laborerId));
        } else {
            sessionStorage.removeItem(HIGHLIGHT_KEY);
        }
    }

    document.querySelectorAll('.laborers-return-link').forEach(link => {
        link.addEventListener('click', () => rememberListPosition(link.getAttribute('data-laborer-id')));
    });

    document.querySelectorAll('.laborers-return-form').forEach(form => {
        form.addEventListener('submit', () => {
            const laborerId = form.getAttribute('data-laborer-id');
            rememberListPosition(laborerId);

            const scrollInput = form.querySelector('.laborers-return-scroll');
            if (scrollInput instanceof HTMLInputElement) {
                scrollInput.value = String(window.scrollY || 0);
            }
        });
    });

    const url = new URL(window.location.href);
    const restoreScrollParam = parseInt(url.searchParams.get('restoreScrollY') || '0', 10);
    const highlightLaborerId = url.searchParams.get('highlightLaborerId');
    const shouldRestore = sessionStorage.getItem(RESTORE_KEY) === '1'
        || (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0)
        || !!highlightLaborerId;

    if (!shouldRestore) {
        return;
    }

    const highlightId = highlightLaborerId || sessionStorage.getItem(HIGHLIGHT_KEY);
    const highlightedRow = highlightId
        ? document.querySelector(`.laborer-row[data-id="${highlightId}"]`)
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

    if ((Number.isFinite(restoreScrollParam) && restoreScrollParam > 0) || highlightLaborerId) {
        url.searchParams.delete('restoreScrollY');
        url.searchParams.delete('highlightLaborerId');
        window.history.replaceState({}, document.title, `${url.pathname}${url.search}`);
    }
})();
