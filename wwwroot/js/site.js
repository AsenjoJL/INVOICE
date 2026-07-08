// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

window.hazelPrint = (function () {
  function getAntiForgeryToken() {
    const host = document.getElementById('hazel-af-token');
    if (!host) return null;
    const input = host.querySelector('input[name="__RequestVerificationToken"]');
    return input ? input.value : null;
  }

  async function prepare() {
    const token = getAntiForgeryToken();
    const headers = {};
    if (token) headers['RequestVerificationToken'] = token;

    const res = await fetch('/PrinterSettings/Prepare', {
      method: 'POST',
      headers
    });

    if (res.ok) return { ok: true };

    let payload = null;
    try { payload = await res.json(); } catch { /* ignore */ }
    return payload || { ok: false, message: 'Selected printer not found. Please update printer settings.' };
  }

  async function prepareAndPrint() {
    try {
      const result = await prepare();
      if (result && result.ok) {
        window.print();
        return false;
      }

      const msg = (result && result.message) ? result.message : 'Selected printer not found. Please update printer settings.';
      alert(msg);

      if (result && result.settingsUrl) {
        window.location.href = result.settingsUrl;
      } else {
        window.location.href = '/PrinterSettings?returnUrl=' + encodeURIComponent(window.location.href);
      }

      return false;
    } catch {
      // If preflight fails (offline/cached), still allow printing.
      window.print();
      return false;
    }
  }

  return { prepareAndPrint };
})();

window.hazelScrollMemory = (function () {
  const storageKey = 'hazelinvoice:scroll-memory';
  const ttlMs = 30 * 1000;
  const maxRestoreAttempts = 12;
  const restoreDelayMs = 50;

  function pageKey(url) {
    return `${url.pathname}${url.search}`;
  }

  function prefersReducedMotion() {
    return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }

  function scrollBehavior() {
    return prefersReducedMotion() ? 'auto' : 'smooth';
  }

  function readMemory() {
    try {
      return JSON.parse(sessionStorage.getItem(storageKey) || 'null');
    } catch {
      return null;
    }
  }

  function writeMemory(targetUrl) {
    const current = new URL(window.location.href);
    const target = targetUrl || current;
    const sameModule = target.pathname === current.pathname;

    if (!sameModule && !target.hash) return;

    sessionStorage.setItem(storageKey, JSON.stringify({
      sourceKey: pageKey(current),
      targetKey: pageKey(target),
      targetHash: target.hash || '',
      x: window.scrollX || 0,
      y: window.scrollY || 0,
      createdAt: Date.now()
    }));
  }

  function getHashTarget(hash) {
    if (!hash) return null;

    try {
      return document.getElementById(decodeURIComponent(hash.slice(1)));
    } catch {
      return document.getElementById(hash.slice(1));
    }
  }

  function getMaxScrollY() {
    const documentHeight = Math.max(
      0,
      document.documentElement.scrollHeight,
      document.body ? document.body.scrollHeight : 0
    );

    return Math.max(0, documentHeight - window.innerHeight);
  }

  function smoothRestore(memory, attempt) {
    const targetElement = getHashTarget(memory.targetHash || '');
    if (targetElement) {
      targetElement.scrollIntoView({ block: 'center', behavior: scrollBehavior() });
      sessionStorage.removeItem(storageKey);
      return;
    }

    const y = parseInt(String(memory.y || 0), 10);
    const x = parseInt(String(memory.x || 0), 10);
    if (!Number.isFinite(y) || y <= 0) {
      sessionStorage.removeItem(storageKey);
      return;
    }

    const maxY = getMaxScrollY();
    if (maxY < y && attempt < maxRestoreAttempts) {
      window.setTimeout(() => smoothRestore(memory, attempt + 1), restoreDelayMs);
      return;
    }

    sessionStorage.removeItem(storageKey);
    window.scrollTo({
      left: Number.isFinite(x) ? x : 0,
      top: Math.min(y, maxY),
      behavior: scrollBehavior()
    });
  }

  function restoreMemory() {
    const memory = readMemory();
    if (!memory || Date.now() - Number(memory.createdAt || 0) > ttlMs) {
      sessionStorage.removeItem(storageKey);
      return;
    }

    const current = new URL(window.location.href);
    const currentKey = pageKey(current);
    const matchesPage = currentKey === memory.sourceKey || currentKey === memory.targetKey;
    const matchesHash = memory.targetHash && current.hash === memory.targetHash;

    if (!matchesPage && !matchesHash) return;

    window.requestAnimationFrame(() => smoothRestore(memory, 0));
  }

  function bind() {
    restoreMemory();

    document.addEventListener('submit', event => {
      const form = event.target;
      if (!(form instanceof HTMLFormElement) || form.dataset.preserveScroll === 'false') return;

      const method = (form.getAttribute('method') || 'get').toLowerCase();
      const action = form.getAttribute('action') || window.location.href;
      const target = new URL(action, window.location.href);

      if (method === 'get') {
        const data = new FormData(form);
        target.search = new URLSearchParams(data).toString();
        writeMemory(target);
        return;
      }

      writeMemory(new URL(window.location.href));
    }, true);

    document.addEventListener('click', event => {
      const clickable = event.target instanceof Element
        ? event.target.closest('a[href], button[type="submit"], input[type="submit"]')
        : null;

      if (!clickable || clickable.dataset.preserveScroll === 'false') return;

      if (clickable instanceof HTMLAnchorElement) {
        if (clickable.target && clickable.target !== '_self') return;
        const target = new URL(clickable.href, window.location.href);
        if (target.origin !== window.location.origin) return;
        if (target.pathname === window.location.pathname && target.search === window.location.search && target.hash) return;
        writeMemory(target);
      }
    }, true);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bind);
  } else {
    bind();
  }

  return { remember: writeMemory, restore: restoreMemory };
})();
