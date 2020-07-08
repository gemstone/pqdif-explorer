// In development, always fetch from the network and do not enable offline support.
// This is because caching would make development more difficult (changes would not
// be reflected on the first load after each change).

self.importScripts('./pqdif-worker.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

async function onInstall(event) {
    console.info('Service worker: Install');
    await self.pqdif.onInstall(event);
}

async function onActivate(event) {
    console.info('Service worker: Activate');
    await clients.claim();
    await self.pqdif.onActivate(event);
}

async function onFetch(event) {
    var pqdifResponse = await self.pqdif.onFetch(event);
    return pqdifResponse || fetch(event.request);
}
