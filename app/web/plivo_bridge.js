// Bridge between the Flutter app and Plivo's Browser SDK.
//
// Plivo ships no Flutter plugin, so on web the app drives the Browser SDK
// through this file (see lib/services/voice/plivo_voice_engine_web.dart). It
// keeps the surface small: a handful of calls in, one callback out. Every
// event reaches Dart as onEvent(type, detailJson).
//
// The SDK itself (about 1.5 MB) is only fetched the first time a Plivo account
// signs in, so Twilio users never download it.
(function () {
  'use strict';

  var SDK_URL = 'https://cdn.plivo.com/sdk/browser/v2/plivo.min.js';
  var LOGIN_TIMEOUT_MS = 20000;

  var sdkLoading = null;
  var client = null;
  var onEvent = function () {};
  // Settles the promise returned by the login() in flight, if any.
  var pendingLogin = null;

  function loadSdk() {
    if (window.Plivo) return Promise.resolve();
    if (sdkLoading) return sdkLoading;

    sdkLoading = new Promise(function (resolve, reject) {
      var script = document.createElement('script');
      script.src = SDK_URL;
      script.async = true;
      script.onload = function () {
        if (window.Plivo) resolve();
        else reject(new Error('The Plivo Browser SDK loaded but did not initialise.'));
      };
      script.onerror = function () {
        sdkLoading = null; // allow a retry once the network is back
        reject(new Error('Could not load the Plivo Browser SDK from ' + SDK_URL + '.'));
      };
      document.head.appendChild(script);
    });
    return sdkLoading;
  }

  function emit(type, detail) {
    try {
      onEvent(type, JSON.stringify(detail || {}));
    } catch (error) {
      console.error('plivoBridge: event handler failed', error);
    }
  }

  function settleLogin(error) {
    if (!pendingLogin) return;
    var pending = pendingLogin;
    pendingLogin = null;
    clearTimeout(pending.timer);
    if (error) pending.reject(error);
    else pending.resolve();
  }

  function createClient() {
    var sdk = new window.Plivo({
      debug: 'ERROR',
      // Ask for the microphone when a call starts rather than on page load.
      permOnClick: true,
      enableTracking: false,
      closeProtection: true,
      maxAverageBitrate: 48000,
    });
    var c = sdk.client;

    c.on('onLogin', function () { settleLogin(); });
    c.on('onLoginFailed', function (cause) {
      settleLogin(new Error('Plivo sign-in failed: ' + (cause || 'unknown reason')));
    });
    c.on('onWebrtcNotSupported', function () {
      settleLogin(new Error('This browser does not support WebRTC calling.'));
    });

    c.on('onCalling', function () { emit('connecting'); });
    c.on('onCallRemoteRinging', function () { emit('ringing'); });
    c.on('onCallAnswered', function () { emit('connected'); });
    c.on('onCallTerminated', function () { emit('ended'); });
    c.on('onCallFailed', function (cause) { emit('ended', { cause: String(cause || '') }); });

    c.on('onIncomingCall', function (callerName) {
      emit('incoming', { from: String(callerName || '') });
    });
    c.on('onIncomingCallCanceled', function () { emit('ended'); });

    c.on('onMediaPermission', function (result) {
      if (result && result.status === 'failure') emit('permissionDenied');
    });
    c.on('onPermissionDenied', function () { emit('permissionDenied'); });

    return c;
  }

  window.plivoBridge = {
    // Resolves once the endpoint is registered and can place and take calls.
    login: function (username, password, handler) {
      onEvent = handler;
      return loadSdk().then(function () {
        if (!client) client = createClient();
        settleLogin(new Error('Superseded by a newer sign-in.'));

        return new Promise(function (resolve, reject) {
          pendingLogin = {
            resolve: resolve,
            reject: reject,
            timer: setTimeout(function () {
              settleLogin(new Error('Timed out signing in to Plivo.'));
            }, LOGIN_TIMEOUT_MS),
          };
          client.login(username, password);
        });
      });
    },

    logout: function () {
      settleLogin(new Error('Signed out.'));
      if (client) client.logout();
    },

    // Prompts for the microphone ahead of a call, then releases it again.
    requestMic: function () {
      if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
        return Promise.reject(new Error('This browser cannot capture audio.'));
      }
      return navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
        stream.getTracks().forEach(function (track) { track.stop(); });
      });
    },

    // The caller id travels as a custom SIP header ("X-PH-" is the prefix Plivo
    // forwards to the answer URL), where the backend turns it into <Dial callerId>.
    call: function (to, callerId) {
      client.call(to, { 'X-PH-CallerId': callerId });
    },

    answer: function () { client.answer(); },

    // One button in the app both declines a ringing call and ends a live one.
    // reject() returns false when there is no unanswered incoming call to decline.
    hangup: function () {
      if (client.reject() === false) client.hangup();
    },

    mute: function () { client.mute(); },
    unmute: function () { client.unmute(); },
    sendDtmf: function (digit) { client.sendDtmf(digit); },
  };
})();
