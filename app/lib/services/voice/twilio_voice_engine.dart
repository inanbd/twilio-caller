import 'dart:async';

import 'package:twilio_voice/twilio_voice.dart';

import '../../core/api_models.dart';
import '../../core/platform_support.dart';
import 'voice_engine.dart';

/// The Twilio Voice SDK, which has Android, iOS, macOS and web implementations.
class TwilioVoiceEngine implements VoiceEngine {
  final _events = StreamController<VoiceEvent>.broadcast();
  StreamSubscription<CallEvent>? _subscription;

  @override
  Stream<VoiceEvent> get events => _events.stream;

  @override
  bool get hasSystemCallUi => PlatformSupport.hasSystemCallUi;

  @override
  Future<void> register(VoiceToken credentials) async {
    await TwilioVoicePlatform.instance.setTokens(accessToken: credentials.token);
    _subscription ??=
        TwilioVoicePlatform.instance.callEventsListener.listen(_onCallEvent);
  }

  @override
  Future<void> unregister() async {
    await _subscription?.cancel();
    _subscription = null;
    await TwilioVoicePlatform.instance.unregister();
  }

  @override
  Future<void> requestPermissions() async {
    // Every platform with the Voice SDK needs the microphone, web included —
    // the browser prompts on getUserMedia, and asking here keeps that prompt
    // out of the middle of dialling.
    if (!await TwilioVoicePlatform.instance.hasMicAccess()) {
      await TwilioVoicePlatform.instance.requestMicAccess();
    }

    // Phone accounts are an Android/iOS concept; there is no system call UI to
    // register with on web or macOS.
    if (!hasSystemCallUi) return;

    if (!await TwilioVoicePlatform.instance.hasRegisteredPhoneAccount()) {
      await TwilioVoicePlatform.instance.registerPhoneAccount();
    }
  }

  @override
  Future<void> placeCall({required String from, required String to}) async {
    // Twilio overwrites From with "client:<identity>" for calls originating in
    // the SDK, so the number to show the recipient travels as CallerId.
    await TwilioVoicePlatform.instance.call.place(
      from: from,
      to: to,
      extraOptions: {'CallerId': from},
    );
  }

  @override
  Future<void> answer() async => TwilioVoicePlatform.instance.call.answer();

  @override
  Future<void> hangUp() async => TwilioVoicePlatform.instance.call.hangUp();

  @override
  Future<void> setMuted(bool muted) async =>
      TwilioVoicePlatform.instance.call.toggleMute(muted);

  @override
  Future<void> setSpeaker(bool on) async =>
      TwilioVoicePlatform.instance.call.toggleSpeaker(on);

  @override
  Future<void> sendDigits(String digits) async =>
      TwilioVoicePlatform.instance.call.sendDigits(digits);

  void _onCallEvent(CallEvent event) {
    switch (event) {
      case CallEvent.incoming:
        final active = TwilioVoicePlatform.instance.call.activeCall;
        _events.add(VoiceEvent(
          VoiceEventType.incoming,
          from: active?.from,
          to: active?.to,
        ));
      case CallEvent.ringing:
        _events.add(const VoiceEvent(VoiceEventType.ringing));
      case CallEvent.connected:
      case CallEvent.answer:
        _events.add(const VoiceEvent(VoiceEventType.connected));
      case CallEvent.reconnecting:
        _events.add(const VoiceEvent(VoiceEventType.reconnecting));
      case CallEvent.reconnected:
        _events.add(const VoiceEvent(VoiceEventType.reconnected));
      case CallEvent.callEnded:
      case CallEvent.declined:
        _events.add(const VoiceEvent(VoiceEventType.ended));
      case CallEvent.mute:
        _events.add(const VoiceEvent(VoiceEventType.muted));
      case CallEvent.unmute:
        _events.add(const VoiceEvent(VoiceEventType.unmuted));
      case CallEvent.speakerOn:
        _events.add(const VoiceEvent(VoiceEventType.speakerOn));
      case CallEvent.speakerOff:
        _events.add(const VoiceEvent(VoiceEventType.speakerOff));
      case CallEvent.permission:
        _events.add(const VoiceEvent(VoiceEventType.permissionDenied));
      default:
        return;
    }
  }

  @override
  void dispose() {
    _subscription?.cancel();
    _events.close();
  }
}
