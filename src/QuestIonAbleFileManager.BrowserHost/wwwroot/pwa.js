const install = document.getElementById('install-app');
const help = document.getElementById('install-help');
const status = document.getElementById('network-status');
let offer;
function network(online) {
  if (status) status.textContent = online
    ? 'Network available. Connect explicitly to read live device state.'
    : 'Offline network. Saved help and a local USB connector may still work. Connect explicitly; no changes are queued.';
}
network(navigator.onLine);
window.addEventListener('offline', () => network(false));
window.addEventListener('online', () => network(true));
window.addEventListener('beforeinstallprompt', event => {
  event.preventDefault(); offer = event;
  if (install) install.hidden = false;
});
install?.addEventListener('click', async () => {
  if (!offer) return;
  const current = offer; offer = null; install.hidden = true;
  try { await current.prompt(); await current.userChoice; }
  catch { if (help) help.textContent = 'Use your browser menu to install or add QFM to your home screen when supported.'; }
});
window.addEventListener('appinstalled', () => {
  offer = null; if (install) install.hidden = true;
  if (help) help.textContent = 'QFM is installed. Keep the local connector running for live host controls.';
});
// Public copies may provide help, but cannot acquire a local host's credentials.
const loopback = location.protocol === 'http:' &&
  (location.hostname === '[::1]' || /^127(?:\.\d{1,3}){3}$/.test(location.hostname));
if (loopback && 'serviceWorker' in navigator) {
  // navigator.onLine can remain true when only the local connector is stopped.
  // Probe inert static content only; never discover devices or reuse credentials.
  fetch('/manifest.webmanifest', {credentials:'omit', cache:'no-store', redirect:'error', headers:{'X-Qfm-Connectivity-Probe':'1'}})
    .catch(() => { if (navigator.serviceWorker.controller && status)
      status.textContent = 'Saved offline shell. Start the local connector, then connect explicitly. No changes are queued.'; });
  navigator.serviceWorker.register('/sw.js', { scope: '/', updateViaCache: 'none' })
    .then(registration => {
      const ready = () => { if (help) help.textContent = 'Offline shell is ready. Install from your browser menu when supported; live controls need an explicit local connector connection.'; };
      const watch = worker => {
        if (!worker) return;
        const changed = () => {
          if (worker.state === 'installed' || worker.state === 'activated') ready();
          else if (worker.state === 'redundant' && !registration.active && help)
            help.textContent = 'Offline shell installation failed. Keep the connector running and reload to try again.';
        };
        worker.addEventListener('statechange', changed); changed();
      };
      if (registration.active || registration.waiting) ready();
      else if (help) help.textContent = 'Preparing the verified offline shell. Keep this page connected until it is ready.';
      watch(registration.installing);
      registration.addEventListener('updatefound', () => watch(registration.installing));
    })
    .catch(() => { if (help) help.textContent = navigator.serviceWorker.controller
      ? 'Using the installed offline shell. Keep the local connector running for live host controls.'
      : 'Offline shell unavailable in this browser session. You can still use connected controls and the CLI.'; });
}
