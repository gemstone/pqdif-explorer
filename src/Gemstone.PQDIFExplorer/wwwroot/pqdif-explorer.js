window.pqdif = (function () {
    var $this = {};

    function toAsyncCallback(callback) {
        var invokeFn, thisArg, staticArgs;
        var instance = callback.instance;
        var methodName = callback.methodName;

        if (instance) {
            // instance.invokeMethodAsync("MethodName", ...)
            invokeFn = instance.invokeMethodAsync;
            thisArg = instance;
            staticArgs = [methodName];
        } else {
            // DotNet.invokeMethodAsync("AssemblyName", "MethodName", ...)
            var assembly = callback.assembly;
            invokeFn = DotNet.invokeMethodAsync;
            staticArgs = [assembly, methodName];
        }

        return async function () {
            var argArray = staticArgs;

            if (arguments.length > 0) {
                var args = Array.from(arguments);
                argArray = staticArgs.concat(args);
            }

            return await invokeFn.apply(thisArg, argArray);
        };
    }

    async function cacheFileListAsync(fileList) {
        var formData = new FormData();

        for (var i = 0; i < fileList.length; i++)
            formData.append("pqdifFile", fileList[i]);

        var promise = new Promise(function (resolve, reject) {
            try {
                var request = new XMLHttpRequest();

                request.onreadystatechange = function () {
                    if (request.readyState !== XMLHttpRequest.DONE)
                        return;

                    if (request.status === 200)
                        resolve(request.response);
                    else
                        reject(request.statusText);
                };

                request.open("POST", "/PQDIF/Cache", true);
                request.responseType = "json";
                request.send(formData);
            } catch (e) {
                reject(e);
            }
        });

        return await promise;
    }

    $this.workerIsReady = async function () {
        await navigator.serviceWorker.ready;

        for (;;) {
            var worker = navigator.serviceWorker.controller;

            if (worker && worker.state === "activated")
                break;

            await new Promise(function (resolve) { setTimeout(resolve, 1); });
        }
    };

    var dropHandlers = {};

    $this.registerDropHandler = function (element, callback) {
        var dropHandler = {
            element: element,
            asyncCallback: toAsyncCallback(callback)
        };

        for (;;) {
            var num = (Math.random() * 0xFFFFFFFF) << 0;
            var id = num.toString(16);

            if (dropHandler[id])
                continue;

            dropHandler.id = id;
            dropHandlers[id] = dropHandler;
            break;
        }

        dropHandler.dragover = function (e) {
            e.preventDefault();
            e.stopPropagation();
        };

        dropHandler.drop = function (e) {
            e.preventDefault();
            e.stopPropagation();
            dropHandler.lastDrop = e;
            dropHandler.asyncCallback();
        };

        element.addEventListener("dragover", dropHandler.dragover);
        element.addEventListener("drop", dropHandler.drop);
        return dropHandler.id;
    };

    $this.unregisterDropHandler = function (id) {
        dropHandler[id] = undefined;
    };

    $this.cacheFilesAsync = async function (fileSource) {
        var files;

        if (typeof (fileSource) === "string") {
            var dropHandler = dropHandlers[fileSource];
            var lastDrop = dropHandler && dropHandler.lastDrop;
            files = lastDrop && lastDrop.dataTransfer.files;
        } else {
            files = fileSource.files;
        }

        if (!files)
            throw "File source not found";

        return await cacheFileListAsync(files);
    };

    $this.triggerClick = function (element) {
        var eventData = {
            view: window,
            bubbles: true,
            cancelable: true
        };

        var event = new MouseEvent("click", eventData);
        element.dispatchEvent(event);
    };

    return $this;
})();
