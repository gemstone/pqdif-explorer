﻿self.pqdif = (function () {
    var $this = {};

    const pqdifCacheName = "pqdif-data-cache";

    async function handlePQDIFRequest(request) {
        var url = new URL(request.url);

        if (!url.pathname.startsWith("/PQDIF"))
            return null;

        if (url.pathname === "/PQDIF/List")
            return await handlePQDIFList(request);

        if (url.pathname === "/PQDIF/Cache")
            return await handlePQDIFCache(request);

        if (url.pathname === "/PQDIF/Purge")
            return await handlePQDIFPurge(request);

        var cache = await caches.open(pqdifCacheName);
        return await cache.match(request);
    }

    async function handlePQDIFList(request) {
        function getFileName(fileKey) {
            var json = atob(fileKey);
            var keyData = JSON.parse(json);
            return keyData.name;
        }

        if (request.method === "GET") {
            const fileEntryPath = "/PQDIF/Retrieve/";
            var cache = await caches.open(pqdifCacheName);
            var keys = await cache.keys();

            var fileEntries = keys
                .map(request => new URL(request.url))
                .map(url => url.pathname)
                .filter(path => path.startsWith(fileEntryPath))
                .map(path => path.substring(fileEntryPath.length))
                .map(key => ({ key: key, name: getFileName(key) }));

            var json = JSON.stringify(fileEntries);
            var blob = new Blob([json], { type: "application/json" });
            return new Response(blob);
        }

        return null;
    }

    async function handlePQDIFCache(request) {
        function generateFileKey(fileName) {
            var keyData = {
                name: fileName,
                salt: Math.random()
            };

            var json = JSON.stringify(keyData);
            return btoa(json);
        }

        if (request.method === "POST") {
            var formData = await request.formData();
            var pqdifFiles = formData.getAll("pqdifFile");

            if (formData.has("pqdifKey") && pqdifFiles.length > 1) {
                return new Response(null, { status: 400 });
            }

            var cache = await caches.open(pqdifCacheName);

            var promises = pqdifFiles.map(async file => {
                var fileKey;
                var cacheRequest;

                for (; ;) {
                    fileKey = formData.get("pqdifKey") || generateFileKey(file.name);
                    cacheRequest = new Request("/PQDIF/Retrieve/" + fileKey);

                    if (formData.has("pqdifKey"))
                        break;

                    var cacheMatch = await cache.match(cacheRequest);

                    if (!cacheMatch)
                        break;
                }

                var cacheHeaders = new Headers({
                    "Content-Length": file.size,
                    "Content-Type": "application/octet-stream",
                    "Content-Disposition": "attachment; filename=" + file.name
                });

                var cacheResponse = new Response(file, { headers: cacheHeaders });
                await cache.put(cacheRequest, cacheResponse);
                return { key: fileKey, name: file.name };
            });

            var fileEntries = await Promise.all(promises);
            var json = JSON.stringify(fileEntries);
            var blob = new Blob([json], { type: "application/json" });
            return new Response(blob);
        }

        return null;
    }

    async function handlePQDIFPurge(request) {
        if (request.method === "DELETE") {
            var fileKey = await request.text();
            var cacheURL = "/PQDIF/Retrieve/" + fileKey;
            var cacheRequest = new Request(cacheURL);
            var cache = await caches.open(pqdifCacheName);

            if (await cache.delete(cacheRequest))
                return new Response();

            return new Response(null, { status: 404 });
        }

        return null;
    }

    $this.onInstall = async function (event) { };
    $this.onActivate = async function (event) { };

    $this.onFetch = async function (event) {
        return await handlePQDIFRequest(event.request);
    };

    return $this;
})();
