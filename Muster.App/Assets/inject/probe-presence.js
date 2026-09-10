// Muster presence reconnaissance probe. Off unless --probe-presence is passed.
//
// Purpose: find out how Teams itself sets your status, so a fallback strategy can use the same
// route rather than clicking through a menu whose markup churns. It is READ ONLY. It never sends
// a request, never changes status, and never reports a header value or a response body — a
// bearer token would be in both.
//
// Same rule as bridge.js: NEVER THROW. This runs inside a live Teams tab, and a broken probe that
// breaks Teams is far worse than an unanswered question.
(function () {
    'use strict';

    var post = function () { };

    try {
        if (!window.chrome || !window.chrome.webview) {
            return;
        }

        post = function (message) {
            try {
                window.chrome.webview.postMessage(message);
            } catch (e) {
                // Host went away. Nothing useful to do.
            }
        };
    } catch (e) {
        return;
    }

    try {
        if (window.__musterPresenceProbe) {
            return;
        }

        Object.defineProperty(window, '__musterPresenceProbe', {
            value: true,
            writable: false,
            enumerable: false,
            configurable: false
        });
    } catch (e) {
        // Double-wrapping is survivable; a hard failure is not.
    }

    // Narrow on purpose. Teams is enormously chatty, and a probe that reported everything would
    // bury the requests worth seeing and sweep up traffic that is none of our business.
    //
    // '/ups/' is the wide one, and it is here deliberately: the first pass matched only URLs
    // containing 'presence' or 'availability', which caught the write and would have missed both
    // the read-back and the reset if either is a plain /ups/{region}/v1/me/ call.
    function isInteresting(url) {
        try {
            var lower = String(url).toLowerCase();
            return lower.indexOf('/ups/') !== -1
                || lower.indexOf('presence') !== -1
                || lower.indexOf('forceavailability') !== -1
                || lower.indexOf('availability') !== -1;
        } catch (e) {
            return false;
        }
    }

    function clip(value, limit) {
        try {
            if (value === null || value === undefined) {
                return '';
            }

            if (typeof value !== 'string') {
                // FormData, Blob and friends. The type alone is the useful part.
                return '[' + Object.prototype.toString.call(value) + ']';
            }

            return value.length > limit ? value.slice(0, limit) + '...' : value;
        } catch (e) {
            return '';
        }
    }

    // Header NAMES only. Which headers Teams sends is exactly what we need to know; what is in
    // them is exactly what must never reach a log file.
    function headerNames(headers) {
        try {
            if (!headers) {
                return [];
            }

            var names = [];

            if (typeof headers.forEach === 'function' && typeof headers.get === 'function') {
                headers.forEach(function (_, name) { names.push(String(name)); });
                return names;
            }

            for (var key in headers) {
                if (Object.prototype.hasOwnProperty.call(headers, key)) {
                    names.push(String(key));
                }
            }

            return names;
        } catch (e) {
            return [];
        }
    }

    // Distinguishes "the wrapper is not running" from "the wrapper is running and Teams simply
    // does not use window.fetch here". Without it, zero reports means both at once and neither
    // can be ruled out. seenAny counts EVERY request, matched or not; only the count is sent.
    var seenAny = 0;
    var lastAnnounced = -1;

    try {
        post({
            kind: 'presence-probe',
            how: 'installed',
            method: 'NONE',
            url: clip(String(location.origin || '') + String(location.pathname || ''), 200),
            body: '',
            headers: '',
            status: 0
        });

        setInterval(function () {
            try {
                if (seenAny !== lastAnnounced) {
                    lastAnnounced = seenAny;
                    post({
                        kind: 'presence-probe',
                        how: 'heartbeat',
                        method: 'NONE',
                        url: clip(String(location.origin || '') + String(location.pathname || ''), 200),
                        body: '',
                        headers: '',
                        status: seenAny
                    });
                }
            } catch (e) {
                // Never let the heartbeat break anything.
            }
        }, 15000);
    } catch (e) {
        // Carry on without a heartbeat.
    }

    function report(how, method, url, body, headers, status) {
        post({
            kind: 'presence-probe',
            how: how,
            method: String(method || 'GET').toUpperCase(),
            url: clip(url, 300),
            body: clip(body, 300),
            headers: headerNames(headers).join(', '),
            status: status === undefined || status === null ? 0 : status
        });
    }

    try {
        var originalFetch = window.fetch;

        if (typeof originalFetch === 'function') {
            window.fetch = function (input, init) {
                var url = '';
                var method = 'GET';
                var body = '';
                var headers = null;

                try {
                    url = typeof input === 'string' ? input : (input && input.url) || '';
                    method = (init && init.method) || (input && input.method) || 'GET';
                    body = init && init.body;
                    headers = (init && init.headers) || (input && input.headers);
                } catch (e) {
                    // Report what was readable.
                }

                var result = originalFetch.apply(this, arguments);

                try {
                    seenAny++;

                    if (isInteresting(url)) {
                        // The status is the point: it tells us whether replaying this request
                        // would actually be accepted, which a DOM click can never tell us.
                        if (result && typeof result.then === 'function') {
                            result.then(
                                function (response) {
                                    report('fetch', method, url, body, headers, response && response.status);
                                },
                                function () {
                                    report('fetch', method, url, body, headers, -1);
                                });
                        } else {
                            report('fetch', method, url, body, headers, 0);
                        }
                    }
                } catch (e) {
                    // Never let reporting break the request itself.
                }

                return result;
            };
        }
    } catch (e) {
        // Leave fetch alone.
    }

    // ---- the SignalR socket -----------------------------------------------------------------
    //
    // Presence changes arrive here rather than in the response to forceavailability, so this is
    // where read-back has to come from. It is also the socket carrying your chat messages, which
    // is why NOTHING from a frame is reported verbatim: only JSON key NAMES, which are structure
    // rather than content, and values that look like an availability word. A frame is never
    // logged whole.
    var framesReported = 0;
    var frameLimit = 40;

    // Deliberately not [A-Za-z]+ over any key: only the handful of words Teams uses for status,
    // so a chat message mentioning one of them contributes the word and nothing around it.
    var availabilityWords = /"(?:availability|activity)"\s*:\s*"([A-Za-z]{2,24})"/g;

    function summarise(frame) {
        var found = [];
        var keys = [];

        try {
            var match;
            availabilityWords.lastIndex = 0;

            while ((match = availabilityWords.exec(frame)) !== null && found.length < 12) {
                found.push(match[1]);
            }

            if (found.length === 0) {
                return null;
            }

            // Key names only. They tell us how to find "is this about me" without carrying a
            // single character of anybody's message.
            var keyPattern = /"([A-Za-z0-9_]{1,40})"\s*:/g;
            var keyMatch;
            var seen = {};

            while ((keyMatch = keyPattern.exec(frame)) !== null && keys.length < 40) {
                if (!seen[keyMatch[1]]) {
                    seen[keyMatch[1]] = true;
                    keys.push(keyMatch[1]);
                }
            }
        } catch (e) {
            return null;
        }

        return { values: found.join(', '), keys: keys.join(', ') };
    }

    function reportFrame(direction, frame) {
        try {
            if (framesReported >= frameLimit || typeof frame !== 'string') {
                return;
            }

            var summary = summarise(frame);

            if (summary === null) {
                return;
            }

            framesReported++;

            post({
                kind: 'presence-frame',
                how: direction,
                values: summary.values,
                headers: summary.keys,
                status: frame.length
            });
        } catch (e) {
            // Never let reporting break the socket.
        }
    }

    try {
        var NativeSocket = window.WebSocket;

        if (typeof NativeSocket === 'function') {
            var WrappedSocket = function (url, protocols) {
                var socket = protocols === undefined
                    ? new NativeSocket(url)
                    : new NativeSocket(url, protocols);

                try {
                    socket.addEventListener('message', function (event) {
                        reportFrame('socket-in', event && event.data);
                    });
                } catch (e) {
                    // Leave the socket working.
                }

                return socket;
            };

            WrappedSocket.prototype = NativeSocket.prototype;
            WrappedSocket.CONNECTING = NativeSocket.CONNECTING;
            WrappedSocket.OPEN = NativeSocket.OPEN;
            WrappedSocket.CLOSING = NativeSocket.CLOSING;
            WrappedSocket.CLOSED = NativeSocket.CLOSED;

            window.WebSocket = WrappedSocket;
        }
    } catch (e) {
        // Leave WebSocket alone. SignalR falls back to long polling anyway, which the fetch and
        // XHR wrappers above already see.
    }

    try {
        var open = XMLHttpRequest.prototype.open;
        var send = XMLHttpRequest.prototype.send;

        XMLHttpRequest.prototype.open = function (method, url) {
            try {
                this.__musterMethod = method;
                this.__musterUrl = url;
                this.__musterHeaders = [];
            } catch (e) {
                // Carry on.
            }

            return open.apply(this, arguments);
        };

        var setHeader = XMLHttpRequest.prototype.setRequestHeader;

        XMLHttpRequest.prototype.setRequestHeader = function (name) {
            try {
                if (this.__musterHeaders) {
                    this.__musterHeaders.push(String(name));
                }
            } catch (e) {
                // Carry on.
            }

            return setHeader.apply(this, arguments);
        };

        XMLHttpRequest.prototype.send = function (body) {
            try {
                seenAny++;

                if (isInteresting(this.__musterUrl)) {
                    var self = this;
                    this.addEventListener('loadend', function () {
                        report('xhr', self.__musterMethod, self.__musterUrl, body, self.__musterHeaders, self.status);
                    });
                }
            } catch (e) {
                // Never let reporting break the request.
            }

            return send.apply(this, arguments);
        };
    } catch (e) {
        // Leave XHR alone.
    }
})();
