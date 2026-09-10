// Muster host bridge.
//
// Registered with AddScriptToExecuteOnDocumentCreatedAsync before the first navigation, so it
// runs before page script in every document, including iframes.
//
// The one rule here: NEVER THROW. A broken wrapper that breaks Teams is far worse than a missing
// notification, so every shim is wrapped, falls back to the original behaviour on error, and
// reports nothing rather than failing loudly.
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
                // The host went away, or the message was not cloneable. Nothing useful to do.
            }
        };
    } catch (e) {
        return;
    }

    // Marks this document so a re-injection cannot double-wrap the same prototypes.
    try {
        if (window.__musterBridge) {
            return;
        }

        Object.defineProperty(window, '__musterBridge', {
            value: { version: 1 },
            writable: false,
            enumerable: false,
            configurable: false
        });
    } catch (e) {
        // If we cannot mark it, carry on: double-wrapping is survivable, a hard failure is not.
    }

    // Strings arriving from the page are untrusted and may be any type at all.
    function text(value, limit) {
        try {
            if (value === null || value === undefined) {
                return '';
            }

            return String(value).slice(0, limit || 500);
        } catch (e) {
            return '';
        }
    }

    // ---- service worker notifications ------------------------------------------------------
    //
    // CoreWebView2.NotificationReceived only covers non-persistent notifications. Anything a
    // service worker raises never reaches the host at all, and that is how Teams delivers most
    // of its messages, so this shim is the only way to see them.
    try {
        var registrationPrototype = window.ServiceWorkerRegistration && window.ServiceWorkerRegistration.prototype;

        if (registrationPrototype && typeof registrationPrototype.showNotification === 'function') {
            var originalShowNotification = registrationPrototype.showNotification;

            registrationPrototype.showNotification = function (title, options) {
                try {
                    var opts = options || {};
                    post({
                        kind: 'notification',
                        title: text(title, 200),
                        body: text(opts.body, 1000),
                        tag: text(opts.tag, 200)
                    });
                } catch (e) {
                    // Reporting is best effort; never let it affect the page.
                }

                return originalShowNotification.apply(this, arguments);
            };
        }
    } catch (e) {
        // Leave showNotification exactly as it was.
    }

    // ---- call detection --------------------------------------------------------------------
    //
    // Microphone acquisition is the call detector. It is a near-perfect proxy for being on a
    // call and, unlike the Teams DOM, it does not move when Microsoft reshuffles the UI. The
    // host counts acquisitions per session, so it is fine that each frame reports its own.
    try {
        var live = [];

        function releaseTrack(track) {
            try {
                if (track.__musterReleased) {
                    return;
                }

                track.__musterReleased = true;

                var at = live.indexOf(track);
                if (at >= 0) {
                    live.splice(at, 1);
                }

                post({ kind: 'media', state: 'released', audio: true, video: false });
            } catch (e) {
                // Never let teardown bookkeeping break the page.
            }
        }

        function watchTrack(track, hasVideo) {
            try {
                if (!track || track.__musterWatched) {
                    return;
                }

                track.__musterWatched = true;
                live.push(track);
                post({ kind: 'media', state: 'acquired', audio: true, video: !!hasVideo });

                // Fires when the device disappears, but NOT when the page calls stop().
                track.addEventListener('ended', function () {
                    releaseTrack(track);
                });

                var originalStop = track.stop;
                if (typeof originalStop === 'function') {
                    track.stop = function () {
                        releaseTrack(track);
                        return originalStop.apply(this, arguments);
                    };
                }
            } catch (e) {
                // A track we cannot watch is a missed call, not a broken page.
            }
        }

        function watchStream(stream) {
            try {
                var audio = stream.getAudioTracks();
                var hasVideo = stream.getVideoTracks().length > 0;

                for (var i = 0; i < audio.length; i++) {
                    watchTrack(audio[i], hasVideo);
                }
            } catch (e) {
                // Ignore.
            }
        }

        var devices = navigator.mediaDevices;

        if (devices && typeof devices.getUserMedia === 'function') {
            var originalGetUserMedia = devices.getUserMedia.bind(devices);

            devices.getUserMedia = function () {
                var result;

                try {
                    result = originalGetUserMedia.apply(null, arguments);
                } catch (e) {
                    // Synchronous failure: hand the page exactly what it would have had.
                    throw e;
                }

                try {
                    return result.then(function (stream) {
                        watchStream(stream);
                        return stream;
                    });
                } catch (e) {
                    return result;
                }
            };
        }

        // A frame going away never sends stop(), so release anything it still holds. Otherwise
        // navigating mid-call would leave the host believing the microphone is still open.
        window.addEventListener('pagehide', function () {
            try {
                var pending = live.slice();
                for (var i = 0; i < pending.length; i++) {
                    releaseTrack(pending[i]);
                }
            } catch (e) {
                // Ignore.
            }
        });
    } catch (e) {
        // Leave getUserMedia exactly as it was.
    }
})();
