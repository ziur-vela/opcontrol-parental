// Copyright (c) 2026 Ziur / OpControl IT Solutions
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

// Service worker: installable app shell (offline page) and Web Push notifications.
// It never caches the dashboard itself; the dashboard always comes live from the server.
const CACHE = 'ocp-shell-v1';
const SHELL = ['/offline.html', '/brand/app-icon-192.png'];

self.addEventListener('install', (event) => {
    event.waitUntil(caches.open(CACHE).then((cache) => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', (event) => {
    event.waitUntil(
        caches.keys()
            .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
            .then(() => self.clients.claim())
    );
});

self.addEventListener('fetch', (event) => {
    const request = event.request;
    if (request.mode !== 'navigate') return;
    event.respondWith(fetch(request).catch(() => caches.match('/offline.html')));
});

self.addEventListener('push', (event) => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch (e) { data = { body: event.data ? event.data.text() : '' }; }
    const title = data.title || 'Parental OpControl';
    event.waitUntil(self.registration.showNotification(title, {
        body: data.body || '',
        icon: '/brand/app-icon-192.png',
        badge: '/brand/push-badge-96.png',
        tag: data.tag || undefined,
        renotify: !!data.tag,
        data: { url: data.url || '/' }
    }));
});

self.addEventListener('notificationclick', (event) => {
    event.notification.close();
    const target = new URL((event.notification.data && event.notification.data.url) || '/', self.location.origin).href;
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of windows) {
            if (new URL(client.url).origin === self.location.origin) {
                await client.focus();
                if ('navigate' in client) {
                    try { await client.navigate(target); } catch (e) { }
                }
                return;
            }
        }
        await self.clients.openWindow(target);
    })());
});
