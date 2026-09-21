import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:twilio_voice/twilio_voice.dart';

import '../core/api_client.dart';
import '../core/api_models.dart';
import '../core/platform_support.dart';

enum CallPhase { idle, connecting, ringing, active, ended }

/// What the call screen renders.
class CallState {
  final CallPhase phase;
  final String peerNumber;
  final String ownedNumber;
  final bool isInbound;
  final bool isMuted;
  final bool isSpeakerOn;
  final DateTime? connectedAt;
  final String? error;

  const CallState({
    this.phase = CallPhase.idle,
    this.peerNumber = '',
    this.ownedNumber = '',
    this.isInbound = false,
    this.isMuted = false,
    this.isSpeakerOn = false,
    this.connectedAt,
    this.error,
  });

  bool get isBusy => phase != CallPhase.idle && phase != CallPhase.ended;

  CallState copyWith({
    CallPhase? phase,
    String? peerNumber,
    String? ownedNumber,
    bool? isInbound,
    bool? isMuted,
    bool? isSpeakerOn,
    DateTime? connectedAt,
    String? error,
    bool clearError = false,
    bool clearConnectedAt = false,
  }) =>
      CallState(
        phase: phase ?? this.phase,
        peerNumber: peerNumber ?? this.peerNumber,
        ownedNumber: ownedNumber ?? this.ownedNumber,
        isInbound: isInbound ?? this.isInbound,
        isMuted: isMuted ?? this.isMuted,
        isSpeakerOn: isSpeakerOn ?? this.isSpeakerOn,
        connectedAt: clearConnectedAt ? null : (connectedAt ?? this.connectedAt),
        error: clearError ? null : (error ?? this.error),
      );
}

/// Wraps the Twilio Voice SDK where it exists, and degrades to backend dial-out
/// where it does not (Linux and Windows have no Voice SDK implementation).
class VoiceService extends ChangeNotifier {
  final ApiClient api;

  CallState _state = const CallState();
  VoiceToken? _token;
  StreamSubscription<CallEvent>? _events;
  bool _registered = false;

  VoiceService({required this.api});

  CallState get state => _state;
  bool get supportsVoip => PlatformSupport.hasVoipSdk;
  bool get isRegistered => _registered;

  /// Fetches an access token and hands it to the SDK so this device can place
  /// calls and be dialled by the backend's TwiML.
  Future<void> register() async {
    if (!supportsVoip) return;

    _token = await api.voiceToken();
    await TwilioVoicePlatform.instance.setTokens(accessToken: _token!.token);
    _registered = true;

    _events ??= TwilioVoicePlatform.instance.callEventsListener.listen(_onCallEvent);

    notifyListeners();
  }

  /// Access tokens are short lived; refresh before one lapses mid-session,
  /// otherwise an inbound call would silently fail to ring.
  Future<void> refreshTokenIfNeeded() async {
    if (!supportsVoip || !_registered) return;
    if (_token != null && !_token!.isExpiring) return;

    try {
      await register();
    } catch (error) {
      debugPrint('Voice token refresh failed: $error');
    }
  }

  Future<void> unregister() async {
    await _events?.cancel();
    _events = null;
    if (_registered) {
      try {
        await TwilioVoicePlatform.instance.unregister();
      } catch (_) {
        // Nothing useful to do if the SDK was never fully up.
      }
      _registered = false;
    }
    _state = const CallState();
    notifyListeners();
  }

  /// Requests the permissions a call needs before one is in flight, so the user
  /// is not prompted while the phone is already ringing.
  Future<void> requestPermissions() async {
    if (!PlatformSupport.hasSystemCallUi) return;

    if (!await TwilioVoicePlatform.instance.hasMicAccess()) {
      await TwilioVoicePlatform.instance.requestMicAccess();
    }
    if (!await TwilioVoicePlatform.instance.hasRegisteredPhoneAccount()) {
      await TwilioVoicePlatform.instance.registerPhoneAccount();
    }
  }

  Future<void> placeCall({required String from, required String to}) async {
    if (!supportsVoip) {
      throw StateError(PlatformSupport.voipUnavailableReason);
    }

    await refreshTokenIfNeeded();
    if (!_registered) await register();

    _state = _state.copyWith(
      phase: CallPhase.connecting,
      peerNumber: to,
      ownedNumber: from,
      isInbound: false,
      clearError: true,
      clearConnectedAt: true,
    );
    notifyListeners();

    // Twilio overwrites From with "client:<identity>" for calls originating in
    // the SDK, so the number to show the recipient travels as CallerId.
    await TwilioVoicePlatform.instance.call.place(
      from: from,
      to: to,
      extraOptions: {'CallerId': from},
    );
  }

  /// The no-VoIP path: Twilio rings the user's own handset, then bridges it to
  /// the destination. Audio never touches this device.
  Future<CallRecord> dialOut({
    required String from,
    required String to,
    required String bridgeTo,
  }) =>
      api.dialOut(from: from, to: to, bridgeTo: bridgeTo);

  Future<void> answer() async => TwilioVoicePlatform.instance.call.answer();

  Future<void> hangUp() async {
    await TwilioVoicePlatform.instance.call.hangUp();
    _state = _state.copyWith(phase: CallPhase.ended, clearConnectedAt: true);
    notifyListeners();
  }

  Future<void> toggleMute() async {
    final next = !_state.isMuted;
    await TwilioVoicePlatform.instance.call.toggleMute(next);
    _state = _state.copyWith(isMuted: next);
    notifyListeners();
  }

  Future<void> toggleSpeaker() async {
    final next = !_state.isSpeakerOn;
    await TwilioVoicePlatform.instance.call.toggleSpeaker(next);
    _state = _state.copyWith(isSpeakerOn: next);
    notifyListeners();
  }

  Future<void> sendDigits(String digits) async =>
      TwilioVoicePlatform.instance.call.sendDigits(digits);

  void _onCallEvent(CallEvent event) {
    switch (event) {
      case CallEvent.incoming:
        final active = TwilioVoicePlatform.instance.call.activeCall;
        _state = _state.copyWith(
          phase: CallPhase.ringing,
          isInbound: true,
          peerNumber: active?.from ?? _state.peerNumber,
          ownedNumber: active?.to ?? _state.ownedNumber,
          clearError: true,
        );
      case CallEvent.ringing:
        _state = _state.copyWith(phase: CallPhase.ringing);
      case CallEvent.connected:
      case CallEvent.answer:
        _state = _state.copyWith(
          phase: CallPhase.active,
          connectedAt: _state.connectedAt ?? DateTime.now(),
        );
      case CallEvent.reconnecting:
        _state = _state.copyWith(phase: CallPhase.connecting);
      case CallEvent.reconnected:
        _state = _state.copyWith(phase: CallPhase.active);
      case CallEvent.callEnded:
      case CallEvent.declined:
        _state = _state.copyWith(phase: CallPhase.ended, clearConnectedAt: true);
      case CallEvent.mute:
        _state = _state.copyWith(isMuted: true);
      case CallEvent.unmute:
        _state = _state.copyWith(isMuted: false);
      case CallEvent.speakerOn:
        _state = _state.copyWith(isSpeakerOn: true);
      case CallEvent.speakerOff:
        _state = _state.copyWith(isSpeakerOn: false);
      case CallEvent.permission:
        _state = _state.copyWith(
          phase: CallPhase.ended,
          error: 'The call needs microphone permission.',
        );
      default:
        return;
    }

    notifyListeners();
  }

  /// Clears a finished call so the UI can leave the call screen.
  void reset() {
    _state = const CallState();
    notifyListeners();
  }

  @override
  void dispose() {
    _events?.cancel();
    super.dispose();
  }
}
