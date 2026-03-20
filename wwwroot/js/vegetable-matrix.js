const vmCfg = window.vmMatrixConfig || {};
const isPrintView = vmCfg.isPrintView === true || vmCfg.isPrintView === 'true';

function submitForPrint() {
    const form = document.getElementById('matrixForm');
    const flag = document.getElementById('doPrint');
    if (!form || !flag) return;

    flag.value = 'true';
    form.submit();
}

function changeOutletPage(d) {
    const p = document.getElementById('hiddenOutletPage');
    p.value = Math.max(1, (parseInt(p.value) || 1) + d);
    document.getElementById('filterForm').submit();
}

function setOutletPage(page) {
    const p = document.getElementById('hiddenOutletPage');
    if (!p) return;
    p.value = Math.max(1, parseInt(page) || 1);
    document.getElementById('filterForm').submit();
}

function changeProductPage(d) {
    const p = document.getElementById('hiddenProductPage');
    p.value = Math.max(1, (parseInt(p.value) || 1) + d);
    document.getElementById('filterForm').submit();
}

(function () {
    'use strict';

    const table = document.getElementById('mainTable');
    const matrixContainer = document.querySelector('.matrix-container');
    if (!table || !matrixContainer) return;

    const elGrandAmt = document.getElementById('grandTotalAmt');
    const elGrandQty = document.getElementById('grandTotalQty');
    const hideZeroPriceBtn = document.getElementById('toggleZeroPriceBtn');
    const fmt = new Intl.NumberFormat('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

    let totalQty = Number(vmCfg.grandTotalQty) || 0;
    let totalAmt = Number(vmCfg.grandTotalAmount) || 0;
    let hideZeroPriceRows = false;
    let dragState = null;

    const prevValues = new WeakMap();
    const rowRefs = new WeakMap();
    function num(v) {
        if (v == null) return 0;
        let s = ('' + v).trim();
        if (s === '') return 0;
        const hasComma = s.includes(',');
        const hasDot = s.includes('.');
        if (hasComma && !hasDot) {
            s = s.replace(',', '.');
        } else if (hasComma && hasDot) {
            s = s.replace(/,/g, '');
        }
        const n = Number(s);
        return Number.isFinite(n) ? n : 0;
    }
    function formatQty(value) {
        if (!Number.isFinite(value) || value === 0) return '';
        return value.toFixed(3).replace(/\.?0+$/, '');
    }

    function applyZeroPriceFilter() {
        const rows = Array.from(table.querySelectorAll('tbody tr'));
        rows.forEach(row => {
            const priceInput = row.querySelector('.price-input');
            const rowPrice = priceInput ? num(priceInput.value) : num(row.dataset.price);
            const hide = hideZeroPriceRows && rowPrice <= 0;
            row.classList.toggle('row-hidden-zero-price', hide);
        });
    }

    function getRowRefs(row) {
        let refs = rowRefs.get(row);
        if (refs) return refs;
        refs = {
            amtCell: row.querySelector('.row-amt-cell'),
            qtyCell: row.querySelector('.row-qty-cell'),
            vegeCell: row.querySelector('.vege-cell')
        };
        rowRefs.set(row, refs);
        return refs;
    }

    table.addEventListener('focusin', (e) => {
        const input = e.target;
        if (!(input instanceof HTMLInputElement)) return;
        if (!input.classList.contains('qty-input') && !input.classList.contains('price-input')) return;
        if (!prevValues.has(input)) prevValues.set(input, num(input.value));
    });

    table.addEventListener('change', (e) => {
        const input = e.target;
        if (!(input instanceof HTMLInputElement)) return;
        if (!input.classList.contains('qty-input') && !input.classList.contains('price-input')) return;
        handle(input);
    });

    table.addEventListener('blur', (e) => {
        const input = e.target;
        if (!(input instanceof HTMLInputElement)) return;
        if (!input.classList.contains('qty-input') && !input.classList.contains('price-input')) return;
        handle(input);
    }, true);

    function handle(input) {
        const row = input.closest('tr');
        if (!row) return;

        const oldVal = prevValues.get(input) ?? 0;
        const newVal = num(input.value);
        if (oldVal === newVal) return;
        prevValues.set(input, newVal);

        let rowQty = num(row.dataset.rowQty);
        let price = num(row.dataset.price);

        const refs = getRowRefs(row);

        if (input.classList.contains('qty-input')) {
            const diff = newVal - oldVal;
            rowQty += diff;
            row.dataset.rowQty = rowQty;

            totalQty += diff;
            totalAmt += (diff * price);
        } else if (input.classList.contains('price-input')) {
            const diff = newVal - oldVal;
            totalAmt += (rowQty * diff);
            price = newVal;
            row.dataset.price = price;
        }

        const rowAmt = rowQty * price;
        if (refs.amtCell) refs.amtCell.textContent = rowAmt !== 0 ? fmt.format(rowAmt) : '-';
        if (refs.qtyCell) refs.qtyCell.textContent = formatQty(rowQty);
        if (elGrandAmt) elGrandAmt.textContent = fmt.format(totalAmt);
        if (elGrandQty) elGrandQty.textContent = totalQty !== 0 ? totalQty.toFixed(2).replace(/\.?0+$/, '') : '0';

        if (refs.vegeCell) {
            refs.vegeCell.classList.remove('status-noorders');
            if (rowQty <= 0) refs.vegeCell.classList.add('status-noorders');
        }

        applyZeroPriceFilter();
    }

    function applyPrintRowFilter() {
        // For print view we want to show ALL items (even zero orders).
        if (isPrintView) return;
        const rows = Array.from(table.querySelectorAll('tbody tr'));
        rows.forEach(row => {
            const rowQty = num(row.dataset.rowQty);
            row.classList.toggle('no-print-row', rowQty <= 0);
        });
    }

    function isInteractiveTarget(target) {
        return !!target.closest('input, select, textarea, button, a, label');
    }

    function startDrag(clientX, clientY) {
        dragState = {
            startX: clientX,
            startY: clientY,
            scrollLeft: matrixContainer.scrollLeft,
            scrollTop: matrixContainer.scrollTop
        };
        matrixContainer.classList.add('matrix-dragging');
    }

    function moveDrag(clientX, clientY) {
        if (!dragState) return;
        const dx = clientX - dragState.startX;
        const dy = clientY - dragState.startY;
        matrixContainer.scrollLeft = dragState.scrollLeft - dx;
        matrixContainer.scrollTop = dragState.scrollTop - dy;
    }

    function endDrag() {
        dragState = null;
        matrixContainer.classList.remove('matrix-dragging');
    }

    matrixContainer.addEventListener('pointerdown', (event) => {
        if (event.pointerType !== 'mouse') return;
        if (event.button !== 0) return;
        if (isInteractiveTarget(event.target)) return;

        startDrag(event.clientX, event.clientY);
        matrixContainer.setPointerCapture?.(event.pointerId);
    });

    matrixContainer.addEventListener('pointermove', (event) => {
        if (!dragState) return;
        moveDrag(event.clientX, event.clientY);
    });

    matrixContainer.addEventListener('pointerup', endDrag);
    matrixContainer.addEventListener('pointercancel', endDrag);
    matrixContainer.addEventListener('pointerleave', endDrag);

    matrixContainer.addEventListener('wheel', (event) => {
        if (event.shiftKey || Math.abs(event.deltaX) > 0) {
            event.preventDefault();
            matrixContainer.scrollLeft += event.deltaX || event.deltaY;
        }
    }, { passive: false });

    if (!isPrintView) {
        window.addEventListener('beforeprint', applyPrintRowFilter);
        window.addEventListener('afterprint', () => {
            const rows = Array.from(table.querySelectorAll('tbody tr'));
            rows.forEach(row => row.classList.remove('no-print-row'));
        });
    }

    if (hideZeroPriceBtn) {
        hideZeroPriceBtn.addEventListener('click', () => {
            hideZeroPriceRows = !hideZeroPriceRows;
            hideZeroPriceBtn.classList.toggle('active', hideZeroPriceRows);
            hideZeroPriceBtn.innerHTML = hideZeroPriceRows
                ? '<i class="bi bi-funnel-fill"></i> Show zero-price items'
                : '<i class="bi bi-funnel"></i> Hide zero-price items';
            applyZeroPriceFilter();
        });
    }
})();

    if (isPrintView) {
        window.addEventListener('load', () => {
            document.body.classList.add('print-three-sheet');
            window.print();
        });
    }

    document.addEventListener('submit', (e) => {
        const flag = document.getElementById('doPrint');
        if (flag && !isPrintView) {
            // Reset to false after submission unless explicitly set by Print
            if (flag.value !== 'true') flag.value = 'false';
        }
    });
