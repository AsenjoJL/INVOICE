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

    function initFillBalance(root = document) {
        root.querySelectorAll('[data-fill-balance-target][data-balance-value]').forEach(btn => {
            if (btn.dataset.fillBalanceBound) return;
            btn.dataset.fillBalanceBound = 'true';
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

    function initAjaxPayrollModalForms(root = document) {
        root.querySelectorAll('form.payroll-modal-form[data-ajax="true"]').forEach(form => {
            if (form.dataset.ajaxFormBound) return;
            form.dataset.ajaxFormBound = 'true';

            form.addEventListener('submit', async event => {
                const outputContainer = form.closest('.modal-body');
                if (!outputContainer) return;
                event.preventDefault();
                const action = form.action || window.location.href;
                const method = (form.method || 'post').toUpperCase();
                const body = new URLSearchParams(new FormData(form));

                outputContainer.innerHTML = '<div class="text-center py-5 text-muted">Saving…</div>';

                try {
                    const response = await fetch(action, {
                        method,
                        headers: {
                            'X-Requested-With': 'XMLHttpRequest',
                            'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8'
                        },
                        body
                    });

                    if (!response.ok) {
                        throw new Error('Unable to save payroll entry.');
                    }

                    const html = await response.text();
                    outputContainer.innerHTML = html;
                    initFillBalance(outputContainer);
                    initAjaxPayrollModalForms(outputContainer);
                } catch (error) {
                    outputContainer.innerHTML = `<div class="text-danger text-center py-5">Unable to save payroll entry. Please try again.</div>`;
                    console.error(error);
                }
            });
        });
    }

    function initPayrollDetailsModal() {
        const modal = document.getElementById('payrollDetailsModal');
        if (!modal) return;

        modal.addEventListener('show.bs.modal', async event => {
            const trigger = event.relatedTarget;
            if (!(trigger instanceof HTMLElement)) return;

            const payrollId = trigger.getAttribute('data-payroll-details-id');
            const body = modal.querySelector('.modal-body');
            if (!body || !payrollId) return;

            body.innerHTML = '<div class="text-center py-5 text-muted">Loading details…</div>';

            try {
                const response = await fetch(`/Payroll/DetailsModal?id=${encodeURIComponent(payrollId)}`, {
                    headers: {
                        'X-Requested-With': 'XMLHttpRequest'
                    }
                });
                if (!response.ok) {
                    throw new Error('Failed to load payroll details.');
                }

                const html = await response.text();
                body.innerHTML = html;
                initFillBalance(body);
                initAjaxPayrollModalForms(body);
            } catch (error) {
                body.innerHTML = `<div class="text-danger text-center py-5">Unable to load details. Please refresh the page and try again.</div>`;
                console.error(error);
            }
        });
    }

    initAutoSubmitFilters();
    initRowLinks();
    initSelectAll();
    initFillBalance();
    initPayrollDetailsModal();
})();
