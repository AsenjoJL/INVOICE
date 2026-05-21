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
