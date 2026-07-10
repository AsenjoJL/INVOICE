(function () {
    'use strict';

    const SCROLL_KEY = 'products:index:scrollY';
    const RESTORE_KEY = 'products:index:restore';
    const STATE_KEY = 'products:index:viewState';

    function getSavedState() {
        try {
            return JSON.parse(sessionStorage.getItem(STATE_KEY) || 'null');
        } catch {
            return null;
        }
    }

    function rememberListPosition() {
        sessionStorage.setItem(SCROLL_KEY, String(window.scrollY || 0));
        sessionStorage.setItem(RESTORE_KEY, '1');
        const state = getSavedState() || {};
        sessionStorage.setItem(STATE_KEY, JSON.stringify({
            ...state,
            scrollY: window.scrollY || 0
        }));
    }

    document.querySelectorAll('.product-edit-link, .product-return-link').forEach(link => {
        link.addEventListener('click', () => {
            rememberListPosition();
        });
    });

    document.querySelectorAll('.product-return-form').forEach(form => {
        form.addEventListener('submit', () => {
            rememberListPosition();
            const input = form.querySelector('.return-client-page');
            const scrollInput = form.querySelector('.return-scroll-y');
            const state = getSavedState() || {};
            const currentPage = Number(state.currentPage || 1);
            const scrollY = Number(state.scrollY || window.scrollY || 0);
            if (input instanceof HTMLInputElement) {
                input.value = Number.isFinite(currentPage) && currentPage > 0
                    ? String(currentPage)
                    : '1';
            }
            if (scrollInput instanceof HTMLInputElement) {
                scrollInput.value = Number.isFinite(scrollY) && scrollY > 0
                    ? String(scrollY)
                    : '0';
            }
        });
    });
})();

(function () {
    'use strict';

    const modalElement = document.getElementById('productEditModal');
    const modalBody = document.getElementById('productEditModalBody');
    if (!modalElement || !modalBody || !window.bootstrap || typeof fetch !== 'function') return;

    const SCROLL_KEY = 'products:index:scrollY';
    const RESTORE_KEY = 'products:index:restore';
    const STATE_KEY = 'products:index:viewState';
    const modal = new window.bootstrap.Modal(modalElement);

    function getSavedState() {
        try {
            return JSON.parse(sessionStorage.getItem(STATE_KEY) || 'null');
        } catch {
            return null;
        }
    }

    function rememberListPosition() {
        const state = getSavedState() || {};
        const scrollY = window.scrollY || 0;
        sessionStorage.setItem(SCROLL_KEY, String(scrollY));
        sessionStorage.setItem(RESTORE_KEY, '1');
        sessionStorage.setItem(STATE_KEY, JSON.stringify({
            ...state,
            scrollY
        }));
    }

    function setLoading() {
        modalBody.innerHTML = '<div class="product-edit-modal-loading">Loading product form...</div>';
    }

    function setSaveState(form, isSaving) {
        const button = form.querySelector('[data-product-edit-save]');
        if (!(button instanceof HTMLButtonElement)) return;

        const label = button.querySelector('.save-label');
        const loading = button.querySelector('.save-loading');
        button.disabled = isSaving;
        label?.classList.toggle('d-none', isSaving);
        loading?.classList.toggle('d-none', !isSaving);
    }

    function showModalMessage(form, message) {
        let alert = form.querySelector('.product-edit-modal-feedback');
        if (!alert) {
            alert = document.createElement('div');
            alert.className = 'alert alert-success product-edit-modal-feedback';
            alert.setAttribute('role', 'alert');
            form.prepend(alert);
        }

        alert.textContent = message || 'Product saved.';
    }

    function syncReturnPage(form) {
        const input = form.querySelector('.return-client-page');
        if (!(input instanceof HTMLInputElement)) return;

        const state = getSavedState() || {};
        const currentPage = Number(state.currentPage || 1);
        input.value = Number.isFinite(currentPage) && currentPage > 0
            ? String(currentPage)
            : '1';
    }

    function parseModalValidation(form) {
        if (window.jQuery?.validator?.unobtrusive) {
            window.jQuery.validator.unobtrusive.parse(form);
        }
    }

    function updateProductRow(product) {
        if (!product?.id) return;

        const rows = Array.from(document.querySelectorAll('tr.product-row'));
        const row = rows.find(item => item.dataset.id === String(product.id));
        if (!(row instanceof HTMLTableRowElement)) return;

        const category = product.category || 'Uncategorized';
        const unit = product.unit || '';
        const deliveryPrice = Number(product.effectiveDeliveryPrice || 0);
        const costText = product.effectiveCostText || '0.00';
        const baseText = product.effectiveBasePriceText || '0.00';
        const deliveryText = product.effectiveDeliveryPriceText || deliveryPrice.toFixed(2);

        row.dataset.sku = product.sku || '';
        row.dataset.name = product.name || '';
        row.dataset.category = category;
        row.dataset.unit = unit;
        row.dataset.cost = String(deliveryPrice);
        row.dataset.status = product.status || (product.isActive ? 'active' : 'inactive');

        const skuCell = row.querySelector('.product-sku');
        if (skuCell) skuCell.textContent = product.sku || '';

        const nameCell = row.querySelector('.product-name');
        if (nameCell) nameCell.textContent = product.name || '';

        const categoryMeta = row.querySelector('td:nth-child(3) .product-meta');
        if (categoryMeta) categoryMeta.textContent = category;

        const unitCell = row.children[3];
        if (unitCell) unitCell.textContent = unit;

        const pricePrimary = row.querySelector('.product-price-primary');
        if (pricePrimary) {
            pricePrimary.textContent = `P${deliveryText}`;
            pricePrimary.classList.toggle('zero', deliveryPrice === 0);
        }

        const priceMeta = row.querySelector('td:nth-child(5) .product-meta');
        if (priceMeta) {
            priceMeta.textContent = `Cost P${costText} · Base P${baseText}`;
            if (product.hasWeeklyPrice) {
                const flag = document.createElement('span');
                flag.className = 'product-weekly-flag';
                flag.textContent = 'Weekly';
                priceMeta.append(' ', flag);
            }
        }

        const statusCell = row.children[5];
        if (statusCell) {
            statusCell.innerHTML = '';
            const pill = document.createElement('span');
            pill.className = `status-pill ${product.isActive ? 'status-active' : 'status-inactive'}`;
            pill.textContent = product.isActive ? 'Active' : 'Inactive';
            statusCell.appendChild(pill);
        }

        row.classList.add('product-row-highlight');
        window.setTimeout(() => row.classList.remove('product-row-highlight'), 2200);
        document.dispatchEvent(new CustomEvent('products:row-updated'));
    }

    function bindModalForm() {
        const form = modalBody.querySelector('form[data-product-edit-modal-form]');
        if (!(form instanceof HTMLFormElement)) return;

        parseModalValidation(form);
        syncReturnPage(form);

        form.addEventListener('submit', async (event) => {
            event.preventDefault();
            rememberListPosition();
            syncReturnPage(form);
            setSaveState(form, true);

            try {
                const response = await fetch(form.action, {
                    method: 'POST',
                    body: new FormData(form),
                    headers: {
                        'X-Requested-With': 'XMLHttpRequest'
                    }
                });

                const contentType = response.headers.get('content-type') || '';
                if (contentType.includes('application/json')) {
                    const result = await response.json();
                    if (result?.ok) {
                        updateProductRow(result.product);
                        showModalMessage(form, result.message);
                        return;
                    }
                }

                const html = await response.text();
                modalBody.innerHTML = html;
                bindModalForm();
            } catch {
                if (form.dataset.fallbackAction) {
                    form.action = form.dataset.fallbackAction;
                }
                form.submit();
            } finally {
                const currentForm = modalBody.querySelector('form[data-product-edit-modal-form]');
                if (currentForm instanceof HTMLFormElement) {
                    setSaveState(currentForm, false);
                }
            }
        });
    }

    document.addEventListener('click', async (event) => {
        const link = event.target instanceof Element
            ? event.target.closest('.product-edit-link[data-edit-modal-url]')
            : null;

        if (!(link instanceof HTMLAnchorElement)) return;
        if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;

        event.preventDefault();
        rememberListPosition();
        setLoading();
        modal.show();

        try {
            const response = await fetch(link.dataset.editModalUrl || link.href, {
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (!response.ok) throw new Error('Unable to load product edit form.');

            modalBody.innerHTML = await response.text();
            bindModalForm();
        } catch {
            window.location.href = link.href;
        }
    });
})();

(function () {
    'use strict';

    const url = new URL(window.location.href);
    const restorePageParam = parseInt(url.searchParams.get('restorePage') || '0', 10);
    const restoreScrollParam = parseInt(url.searchParams.get('restoreScrollY') || '0', 10);
    const highlightProductId = url.searchParams.get('highlightProductId');

    const STATUS_FILTER = {
        ALL: 'all',
        ACTIVE: 'active',
        INACTIVE: 'inactive',
        ZERO: 'zero'
    };

    const SORT = {
        ASC: 'asc',
        DESC: 'desc'
    };

    const tableBody = document.getElementById('productsTableBody');
    if (!tableBody) return;

    const rows = Array.from(tableBody.querySelectorAll('tr.product-row'));
    if (!rows.length) return;

    const statusTabs = Array.from(document.querySelectorAll('[data-status-filter]'));
    const categoryFilter = document.getElementById('categoryFilter');
    const searchInput = document.getElementById('searchInput');
    const rowSummary = document.getElementById('rowSummary');
    const pageSizeSelect = document.getElementById('pageSizeSelect');
    const prevPageBtn = document.getElementById('prevPageBtn');
    const nextPageBtn = document.getElementById('nextPageBtn');
    const pageInfo = document.getElementById('pageInfo');
    const selectAllRows = document.getElementById('selectAllRows');
    const bulkToolbar = document.getElementById('bulkToolbar');
    const bulkCount = document.getElementById('bulkCount');
    const clearSelectionBtn = document.getElementById('clearSelectionBtn');
    const sortTriggers = Array.from(document.querySelectorAll('.sort-trigger'));

    const state = {
        activeStatusFilter: STATUS_FILTER.ALL,
        currentPage: 1,
        pageSize: parseInt(pageSizeSelect?.value || '25', 10),
        sortKey: 'name',
        sortDir: SORT.ASC,
        selectedIds: new Set()
    };
    const hasExplicitRestoreParams =
        (Number.isFinite(restorePageParam) && restorePageParam > 0) ||
        (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0) ||
        !!highlightProductId;
    const shouldRestore = sessionStorage.getItem('products:index:restore') === '1' || hasExplicitRestoreParams;

    function loadViewState() {
        if (shouldRestore) {
            try {
                const saved = JSON.parse(sessionStorage.getItem('products:index:viewState') || 'null');
                if (saved) {
                    if (saved.activeStatusFilter) state.activeStatusFilter = saved.activeStatusFilter;
                    if (Number.isFinite(saved.currentPage) && saved.currentPage > 0) state.currentPage = saved.currentPage;
                    if (Number.isFinite(saved.pageSize) && saved.pageSize > 0) state.pageSize = saved.pageSize;
                    if (saved.sortKey) state.sortKey = saved.sortKey;
                    if (saved.sortDir) state.sortDir = saved.sortDir;

                    if (searchInput && typeof saved.searchTerm === 'string') searchInput.value = saved.searchTerm;
                    if (categoryFilter && typeof saved.categoryValue === 'string') categoryFilter.value = saved.categoryValue;
                    if (pageSizeSelect) pageSizeSelect.value = String(state.pageSize);
                }
            } catch {
            }
        }

        if (Number.isFinite(restorePageParam) && restorePageParam > 0) {
            state.currentPage = restorePageParam;
        }
    }

    function saveViewState() {
        sessionStorage.setItem('products:index:viewState', JSON.stringify({
            activeStatusFilter: state.activeStatusFilter,
            currentPage: state.currentPage,
            pageSize: state.pageSize,
            sortKey: state.sortKey,
            sortDir: state.sortDir,
            searchTerm: searchInput?.value || '',
            categoryValue: categoryFilter?.value || '',
            scrollY: window.scrollY || 0
        }));
    }

    function toNumber(value) {
        const n = Number(value);
        return Number.isFinite(n) ? n : 0;
    }

    function norm(value) {
        return (value || '').toString().toLowerCase().trim();
    }

    function rowMatchesFilters(row) {
        const status = norm(row.dataset.status);
        const category = row.dataset.category || '';
        const cost = toNumber(row.dataset.cost);
        const query = norm(searchInput?.value || '');
        const categoryValue = categoryFilter?.value || '';

        if (state.activeStatusFilter === STATUS_FILTER.ACTIVE && status !== STATUS_FILTER.ACTIVE) return false;
        if (state.activeStatusFilter === STATUS_FILTER.INACTIVE && status !== STATUS_FILTER.INACTIVE) return false;
        if (state.activeStatusFilter === STATUS_FILTER.ZERO && cost !== 0) return false;

        if (categoryValue && category !== categoryValue) return false;

        if (query) {
            const haystack = [
                row.dataset.sku,
                row.dataset.name,
                row.dataset.category,
                row.dataset.unit
            ].join(' ').toLowerCase();
            if (!haystack.includes(query)) return false;
        }

        return true;
    }

    function compareRows(a, b) {
        if (state.sortKey === 'cost') {
            const av = toNumber(a.dataset.cost);
            const bv = toNumber(b.dataset.cost);
            return state.sortDir === SORT.ASC ? av - bv : bv - av;
        }

        const keyA = norm(a.dataset[state.sortKey] || '');
        const keyB = norm(b.dataset[state.sortKey] || '');
        if (keyA === keyB) return 0;
        if (state.sortDir === SORT.ASC) return keyA > keyB ? 1 : -1;
        return keyA < keyB ? 1 : -1;
    }

    function getVisibleRows() {
        return rows.filter(r => r.style.display !== 'none');
    }

    function updateBulkToolbar() {
        if (!bulkToolbar || !bulkCount) return;
        const count = state.selectedIds.size;
        bulkToolbar.classList.toggle('show', count > 0);
        bulkCount.textContent = `${count} selected`;
    }

    function syncSelectAll(visibleRows) {
        if (!selectAllRows) return;
        if (!visibleRows.length) {
            selectAllRows.checked = false;
            selectAllRows.indeterminate = false;
            return;
        }
        const selectedVisible = visibleRows.filter(r => {
            const id = r.dataset.id;
            return id && state.selectedIds.has(id);
        }).length;
        selectAllRows.checked = selectedVisible === visibleRows.length;
        selectAllRows.indeterminate = selectedVisible > 0 && selectedVisible < visibleRows.length;
    }

    function refreshTable() {
        const filtered = rows.filter(rowMatchesFilters).sort(compareRows);
        const total = rows.length;
        const filteredCount = filtered.length;

        const pageCount = Math.max(1, Math.ceil(filteredCount / state.pageSize));
        state.currentPage = Math.max(1, Math.min(state.currentPage, pageCount));
        const start = (state.currentPage - 1) * state.pageSize;
        const end = start + state.pageSize;
        const pageRows = filtered.slice(start, end);
        const visibleSet = new Set(pageRows);

        rows.forEach(r => {
            r.style.display = visibleSet.has(r) ? '' : 'none';
        });

        if (rowSummary) {
            rowSummary.textContent = `Showing ${filteredCount.toLocaleString()} of ${total.toLocaleString()} products`;
        }
        if (pageInfo) {
            pageInfo.textContent = `Page ${state.currentPage} of ${pageCount}`;
        }
        if (prevPageBtn) prevPageBtn.disabled = state.currentPage <= 1;
        if (nextPageBtn) nextPageBtn.disabled = state.currentPage >= pageCount;

        syncSelectAll(pageRows);
        updateBulkToolbar();
        saveViewState();
    }

    statusTabs.forEach(tab => {
        tab.addEventListener('click', () => {
            state.activeStatusFilter = tab.dataset.statusFilter || STATUS_FILTER.ALL;
            statusTabs.forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            state.currentPage = 1;
            refreshTable();
        });
    });

    if (categoryFilter) {
        categoryFilter.addEventListener('change', () => {
            state.currentPage = 1;
            refreshTable();
        });
    }

    if (searchInput) {
        searchInput.addEventListener('input', () => {
            state.currentPage = 1;
            refreshTable();
        });
    }

    if (pageSizeSelect) {
        pageSizeSelect.addEventListener('change', () => {
            state.pageSize = Math.max(1, parseInt(pageSizeSelect.value || '25', 10));
            state.currentPage = 1;
            refreshTable();
        });
    }

    if (prevPageBtn) {
        prevPageBtn.addEventListener('click', () => {
            state.currentPage = Math.max(1, state.currentPage - 1);
            refreshTable();
        });
    }

    if (nextPageBtn) {
        nextPageBtn.addEventListener('click', () => {
            state.currentPage += 1;
            refreshTable();
        });
    }

    if (selectAllRows) {
        selectAllRows.addEventListener('change', () => {
            const visibleRows = getVisibleRows();
            visibleRows.forEach(row => {
                const id = row.dataset.id;
                const cb = row.querySelector('.row-check');
                if (!id || !cb) return;
                cb.checked = selectAllRows.checked;
                if (selectAllRows.checked) state.selectedIds.add(id);
                else state.selectedIds.delete(id);
            });
            updateBulkToolbar();
        });
    }

    tableBody.addEventListener('change', (e) => {
        const target = e.target;
        if (!(target instanceof HTMLInputElement) || !target.classList.contains('row-check')) return;
        const id = target.dataset.id;
        if (!id) return;
        if (target.checked) state.selectedIds.add(id);
        else state.selectedIds.delete(id);
        syncSelectAll(getVisibleRows());
        updateBulkToolbar();
    });

    if (clearSelectionBtn) {
        clearSelectionBtn.addEventListener('click', () => {
            state.selectedIds.clear();
            rows.forEach(r => {
                const cb = r.querySelector('.row-check');
                if (cb) cb.checked = false;
            });
            if (selectAllRows) {
                selectAllRows.checked = false;
                selectAllRows.indeterminate = false;
            }
            updateBulkToolbar();
        });
    }

    document.addEventListener('products:row-updated', () => {
        refreshTable();
    });

    sortTriggers.forEach(trigger => {
        trigger.addEventListener('click', () => {
            const key = trigger.dataset.sortKey;
            if (!key) return;
            if (state.sortKey === key) {
                state.sortDir = state.sortDir === SORT.ASC ? SORT.DESC : SORT.ASC;
            } else {
                state.sortKey = key;
                state.sortDir = SORT.ASC;
            }

            sortTriggers.forEach(t => {
                t.classList.remove('active');
                const icon = t.querySelector('.bi');
                if (icon) icon.className = 'bi bi-arrow-down-up';
            });

            trigger.classList.add('active');
            const activeIcon = trigger.querySelector('.bi');
            if (activeIcon) activeIcon.className = state.sortDir === SORT.ASC ? 'bi bi-arrow-up' : 'bi bi-arrow-down';

            refreshTable();
        });
    });

    loadViewState();
    statusTabs.forEach(tab => {
        tab.classList.toggle('active', tab.dataset.statusFilter === state.activeStatusFilter);
    });
    sortTriggers.forEach(t => {
        t.classList.remove('active');
        const icon = t.querySelector('.bi');
        if (icon) icon.className = 'bi bi-arrow-down-up';
    });
    const activeSort = sortTriggers.find(t => t.dataset.sortKey === state.sortKey);
    if (activeSort) {
        activeSort.classList.add('active');
        const activeIcon = activeSort.querySelector('.bi');
        if (activeIcon) activeIcon.className = state.sortDir === SORT.ASC ? 'bi bi-arrow-up' : 'bi bi-arrow-down';
    }

    refreshTable();

    if (shouldRestore) {
        const saved = (() => {
            try { return JSON.parse(sessionStorage.getItem('products:index:viewState') || 'null'); } catch { return null; }
        })();
        const highlightedRow = highlightProductId
            ? tableBody.querySelector(`tr.product-row[data-id="${highlightProductId}"]`)
            : null;

        if (highlightedRow instanceof HTMLElement && highlightedRow.style.display !== 'none') {
            window.requestAnimationFrame(() => {
                highlightedRow.scrollIntoView({ behavior: 'smooth', block: 'center' });
                highlightedRow.classList.add('product-row-highlight');
                window.setTimeout(() => highlightedRow.classList.remove('product-row-highlight'), 2200);
            });
        } else {
            const y = parseInt(String(restoreScrollParam > 0 ? restoreScrollParam : (saved?.scrollY ?? sessionStorage.getItem('products:index:scrollY') ?? '0')), 10);
            if (!Number.isNaN(y) && y > 0) {
                window.requestAnimationFrame(() => window.scrollTo(0, y));
            }
        }

        sessionStorage.removeItem('products:index:restore');
        sessionStorage.removeItem('products:index:scrollY');
    }

    if ((Number.isFinite(restorePageParam) && restorePageParam > 0) || (Number.isFinite(restoreScrollParam) && restoreScrollParam > 0) || highlightProductId) {
        url.searchParams.delete('restorePage');
        url.searchParams.delete('restoreScrollY');
        url.searchParams.delete('highlightProductId');
        window.history.replaceState({}, document.title, `${url.pathname}${url.search}${url.hash}`);
    }
})();
