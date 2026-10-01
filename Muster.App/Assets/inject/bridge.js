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

        // ---- level metering ----------------------------------------------------------------
        //
        // Taps the captured track so the shell can show whether the microphone is actually
        // picking anything up, which is the question Teams makes you run a test call to answer.
        //
        // Three things here are deliberate. The analyser is NEVER connected to the context's
        // destination — that would play the microphone back through the speakers mid-call. Only
        // one track is metered per frame, because a frame has one microphone and an audio graph
        // per track would be a real cost inside someone's meeting. And the track's enabled flag
        // is reported alongside the level, because Teams' mute button leaves the track open and
        // feeding digital silence: without it, muted and broken look identical.
        var meter = null;

        function stopMeter() {
            try {
                if (!meter) {
                    return;
                }

                var stopping = meter;
                meter = null;

                clearInterval(stopping.timer);

                try {
                    stopping.source.disconnect();
                } catch (e) {
                    // Already torn down with the track.
                }

                // Closed rather than left idle: an AudioContext holds an output device open, and
                // one outliving the call it was opened for is exactly the kind of thing that
                // makes an audio problem look like the shell's fault.
                try {
                    stopping.context.close();
                } catch (e) {
                    // Nothing useful to do.
                }
            } catch (e) {
                meter = null;
            }
        }

        function startMeter(track) {
            try {
                var Ctx = window.AudioContext || window.webkitAudioContext;

                if (!Ctx || !track) {
                    return;
                }

                var context = new Ctx();

                // getUserMedia has already succeeded, so a gesture has happened and this should
                // be running. Resume anyway, and swallow the rejection: a suspended context costs
                // a meter, not a call.
                try {
                    if (context.state === 'suspended' && context.resume) {
                        var resumed = context.resume();
                        if (resumed && resumed.catch) {
                            resumed.catch(function () { });
                        }
                    }
                } catch (e) {
                    // Carry on.
                }

                var source = context.createMediaStreamSource(new MediaStream([track]));
                var analyser = context.createAnalyser();
                analyser.fftSize = 512;
                analyser.smoothingTimeConstant = 0;
                source.connect(analyser);

                var samples = new Float32Array(analyser.fftSize);
                var peak = 0;
                var sent = 0;

                // Sampled far faster than it is reported, and the peak between reports is what
                // goes out: at four reports a second, the average across a short word is almost
                // nothing.
                var timer = setInterval(function () {
                    try {
                        analyser.getFloatTimeDomainData(samples);

                        var sum = 0;
                        for (var i = 0; i < samples.length; i++) {
                            sum += samples[i] * samples[i];
                        }

                        var rms = Math.sqrt(sum / samples.length);
                        if (rms > peak) {
                            peak = rms;
                        }

                        var now = Date.now();
                        if (now - sent < 250) {
                            return;
                        }

                        sent = now;
                        post({
                            kind: 'media-level',
                            level: peak > 1 ? 1 : peak,
                            enabled: track.enabled !== false
                        });
                        peak = 0;
                    } catch (e) {
                        // One bad tick means a broken graph rather than a blip. Stop, rather than
                        // post nonsense four times a second for the rest of the call.
                        stopMeter();
                    }
                }, 50);

                meter = { context: context, source: source, track: track, timer: timer };
            } catch (e) {
                // A meter that will not start is a missing bar, nothing more.
                stopMeter();
            }
        }

        // Keeps one meter running on whichever track is still live, so swapping headsets
        // mid-call moves the meter across rather than ending it.
        function ensureMeter() {
            try {
                if (meter && live.indexOf(meter.track) >= 0) {
                    return;
                }

                stopMeter();

                if (live.length > 0) {
                    startMeter(live[0]);
                }
            } catch (e) {
                // Ignore.
            }
        }

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

                ensureMeter();
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
                ensureMeter();

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

                stopMeter();
            } catch (e) {
                // Ignore.
            }
        });
    } catch (e) {
        // Leave getUserMedia exactly as it was.
    }
})();
