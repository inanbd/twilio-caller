import 'dart:async';
import 'dart:convert';
import 'dart:js_interop';

import '../../core/api_models.dart';
import 'plivo_event.dart';
import 'voice_engine.dart';

/// `window.plivoBridge`, defined in web/plivo_bridge.js. The Browser SDK is an
/// event-emitter with a large surface; the bridge narrows it to a few calls and
/// one callback so that this file needs no knowledge of the SDK's own types.
@JS('plivoBridge')
external _PlivoBridge? get _bridge;

extension type _PlivoBridge._(JSObject _) implements JSObject {
  external JSPromise<JSAny?> login(
    JSString username,
    JSString password,
    JSFunction onEvent,
  );
  external void logout();
  external JSPromise<JSAny?> requestMic();
  external void call(JSString to, JSString callerId);
  external void answer();
  external void hangup();
  external void mute();
  external void unmute();
  external void sendDtmf(JSString digit);
}

VoiceEngine createPlivoVoiceEngine() => PlivoWebVoiceEngine();

class PlivoWebVoiceEngine implements VoiceEngine {
  final _events = StreamController<VoiceEvent>.broadcast();

  @override
  Stream<VoiceEvent> get events => _events.stream;

  /// A browser has no system call UI; the page is the call UI.
  @override
  bool get hasSystemCallUi => false;

  _PlivoBridge get _plivo =>
      _bridge ??
      (throw StateError(
        'plivo_bridge.js is not loaded. Check that web/index.html includes it.',
      ));

  @override
  Future<void> register(VoiceToken credentials) async {
    final username = credentials.username;
    final password = credentials.password;
    if (username == null || password == null) {
      throw StateError('The backend returned no Plivo endpoint to sign in with.');
    }

    await _plivo.login(username.toJS, password.toJS, _onEvent.toJS).toDart;
  }

  @override
  Future<void> unregister() async => _plivo.logout();

  @override
  Future<void> requestPermissions() async {
    await _plivo.requestMic().toDart;
  }

  @override
  Future<void> placeCall({required String from, required String to}) async =>
      _plivo.call(plivoDigits(to).toJS, plivoDigits(from).toJS);

  @override
  Future<void> answer() async => _plivo.answer();

  @override
  Future<void> hangUp() async => _plivo.hangup();

  @override
  Future<void> setMuted(bool muted) async {
    muted ? _plivo.mute() : _plivo.unmute();
    // The SDK has no mute event of its own to confirm with.
    _events.add(VoiceEvent(muted ? VoiceEventType.muted : VoiceEventType.unmuted));
  }

  /// The browser, not the page, chooses the output device.
  @override
  Future<void> setSpeaker(bool on) async {}

  @override
  Future<void> sendDigits(String digits) async {
    for (final digit in digits.split('')) {
      _plivo.sendDtmf(digit.toJS);
    }
  }

  void _onEvent(JSString type, JSString detail) {
    final decoded = jsonDecode(detail.toDart);
    final event = plivoEventToVoiceEvent(
      type.toDart,
      decoded is Map<String, dynamic> ? decoded : const {},
    );
    if (event != null) _events.add(event);
  }

  @override
  void dispose() => _events.close();
}
