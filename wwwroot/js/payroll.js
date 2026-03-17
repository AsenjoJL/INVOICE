(() => {
    'use strict';

    function isInteractive(target) {
        return Boolean(target.closest('a,button,input,select,textarea,label,.dropdown-menu'));
    }

    function initAutoSubmitFilters() {
        document.querySelectorAll('form[data-auto-submit="true"]').forEach(form => {
            form.querySelectorAll('input[type="date"], select').forEach(control => {
                control.addEventListener('change', () => form.requestSubmit());
            });
        });
    }

    function initRowLinks() {
        document.querySelectorAll('tr[data-row-link]').forEach(row => {
            const url = row.getAttribute('data-row-link');
            if (!url) return;
            row.addEventListener('click', e => {
                if (isInteractive(e.target)) return;
                window.location.href = url;
            });
        });
    }

    function initSelectAll() {
        document.querySelectorAll('input[data-select-all-target]').forEach(master => {
            const selector = master.getAttribute('data-select-all-target');
            if (!selector) return;
            master.addEventListener('change', () => {
                document.querySelectorAll(selector).forEach(item => {
                    if (item instanceof HTMLInputElement) {
                        item.checked = master.checked;
                    }
                });
            });
        });
    }

    function initFillBalance() {
        document.querySelectorAll('[data-fill-balance-target][data-balance-value]').forEach(btn => {
            btn.addEventListener('click', () => {
                const targetSelector = btn.getAttribute('data-fill-balance-target');
                const value = btn.getAttribute('data-balance-value');
                if (!targetSelector || value == null) return;
                const input = document.querySelector(targetSelector);
                if (!(input instanceof HTMLInputElement)) return;
                input.value = value;
                input.focus();
            });
        });
    }

    initAutoSubmitFilters();
    initRowLinks();
    initSelectAll();
    initFillBalance();
})();
