import 'dart:async';

import 'package:flutter/foundation.dart';

import '../core/api_client.dart';
import '../core/api_models.dart';
import '../core/platform_support.dart';
import 'voice/plivo_voice_engine.dart';
import 'voice/twilio_voice_engine.dart';
import 'voice/voice_engine.dart';

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

/// Owns the state of the call in progress and drives whichever voice SDK matches
/// the connected provider. Where no SDK exists for this platform it degrades to
/// backend dial-out instead.
class VoiceService extends ChangeNotifier {
  final ApiClient api;
  final VoiceEngine Function(String provider) _createEngine;

  String _provider = TelephonyProvider.twilio;
  final bool? _supportsVoipOverride;
  VoiceEngine? _engine;
  StreamSubscription<VoiceEvent>? _events;

  CallState _state = const CallState();
  VoiceToken? _token;
  bool _registered = false;

  /// [engineFactory] and [supportsVoip] exist for tests; by default both follow
  /// the provider and the platform.
  VoiceService({
    required this.api,
    VoiceEngine Function(String provider)? engineFactory,
    bool? supportsVoip,
  })  : _createEngine = engineFactory ?? _defaultEngine,
        _supportsVoipOverride = supportsVoip;

  static VoiceEngine _defaultEngine(String provider) =>
      provider == TelephonyProvider.plivo ? createPlivoVoiceEngine() : TwilioVoiceEngine();

  CallState get state => _state;
  String get provider => _provider;
  bool get supportsVoip =>
      _supportsVoipOverride ?? PlatformSupport.hasVoipSdk(_provider);
  bool get isRegistered => _registered;

  /// False where the page or window itself is the call UI (web, macOS).
  bool get hasSystemCallUi => _engine?.hasSystemCallUi ?? PlatformSupport.hasSystemCallUi;

  /// Why calls cannot use this device's microphone, when they cannot.
  String get voipUnavailableReason => PlatformSupport.voipUnavailableReason(_provider);

  /// Selects the provider whose SDK calls go through. Known only once a session
  /// exists, which is after this service is built.
  Future<void> useProvider(String provider) async {
    if (provider == _provider) return;

    await unregister();
    await _events?.cancel();
    _events = null;
    _engine?.dispose();
    _engine = null;
    _provider = provider;
    notifyListeners();
  }

  VoiceEngine get _voip {
    final existing = _engine;
    if (existing != null) return existing;

    final engine = _createEngine(_provider);
    _events = engine.events.listen(_onVoiceEvent);
    return _engine = engine;
  }

  /// Fetches credentials and hands them to the SDK so this device can place
  /// calls and be rung by the backend.
  Future<void> register() async {
    if (!supportsVoip) return;

    _token = await api.voiceToken();
    await _voip.register(_token!);
    _registered = true;

    notifyListeners();
  }

  /// Twilio access tokens are short lived; refresh before one lapses
  /// mid-session, otherwise an inbound call would silently fail to ring.
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
    if (_registered) {
      try {
        await _engine?.unregister();
      } catch (_) {
        // Nothing useful to do if the SDK was never fully up.
      }
      _registered = false;
    }
    _token = null;
    _state = const CallState();
    notifyListeners();
  }

  /// Requests the permissions a call needs before one is in flight, so the user
  /// is not prompted while the phone is already ringing.
  Future<void> requestPermissions() async {
    if (!supportsVoip) return;
    await _voip.requestPermissions();
  }

  Future<void> placeCall({required String from, required String to}) async {
    if (!supportsVoip) {
      throw StateError(voipUnavailableReason);
    }

    await refreshTokenIfNeeded();
    if (!_registered) await register();

    _state = _state.copyWith(
      phase: CallPhase.connecting,
      peerNumber: to,
      ownedNumber: from,
      isInbound: false,
      isMuted: false,
      clearError: true,
      clearConnectedAt: true,
    );
    notifyListeners();

    try {
      await _voip.placeCall(from: from, to: to);
    } catch (_) {
      // Never leave the call screen up for a call that did not start.
      _state = const CallState();
      notifyListeners();
      rethrow;
    }
  }

  /// The no-VoIP path: the provider rings the user's own handset, then bridges
  /// it to the destination. Audio never touches this device.
  Future<CallRecord> dialOut({
    required String from,
    required String to,
    required String bridgeTo,
  }) =>
      api.dialOut(from: from, to: to, bridgeTo: bridgeTo);

  Future<void> answer() async {
    // On Android and iOS the system call UI has already secured the microphone
    // by the time the user can answer. On web and macOS nothing has asked yet,
    // so an ungranted permission would connect the call to silence.
    if (!hasSystemCallUi) {
      await requestPermissions();
    }

    await _voip.answer();
  }

  Future<void> hangUp() async {
    await _voip.hangUp();
    _state = _state.copyWith(phase: CallPhase.ended, clearConnectedAt: true);
    notifyListeners();
  }

  Future<void> toggleMute() async {
    final next = !_state.isMuted;
    await _voip.setMuted(next);
    _state = _state.copyWith(isMuted: next);
    notifyListeners();
  }

  Future<void> toggleSpeaker() async {
    final next = !_state.isSpeakerOn;
    await _voip.setSpeaker(next);
    _state = _state.copyWith(isSpeakerOn: next);
    notifyListeners();
  }

  Future<void> sendDigits(String digits) async => _voip.sendDigits(digits);

  void _onVoiceEvent(VoiceEvent event) {
    switch (event.type) {
      case VoiceEventType.incoming:
        _state = _state.copyWith(
          phase: CallPhase.ringing,
          isInbound: true,
          isMuted: false,
          peerNumber: event.from ?? _state.peerNumber,
          ownedNumber: event.to ?? _state.ownedNumber,
          clearError: true,
          clearConnectedAt: true,
        );
      case VoiceEventType.connecting:
        // An SDK may announce "connecting" after we already showed it.
        if (_state.phase == CallPhase.active) return;
        _state = _state.copyWith(phase: CallPhase.connecting);
      case VoiceEventType.ringing:
        _state = _state.copyWith(phase: CallPhase.ringing);
      case VoiceEventType.connected:
        _state = _state.copyWith(
          phase: CallPhase.active,
          connectedAt: _state.connectedAt ?? DateTime.now(),
        );
      case VoiceEventType.reconnecting:
        _state = _state.copyWith(phase: CallPhase.connecting);
      case VoiceEventType.reconnected:
        _state = _state.copyWith(phase: CallPhase.active);
      case VoiceEventType.ended:
        // A late "ended" for a call the user already dismissed is not news.
        if (_state.phase == CallPhase.idle) return;
        _state = _state.copyWith(
          phase: CallPhase.ended,
          error: event.error,
          clearConnectedAt: true,
        );
      case VoiceEventType.muted:
        _state = _state.copyWith(isMuted: true);
      case VoiceEventType.unmuted:
        _state = _state.copyWith(isMuted: false);
      case VoiceEventType.speakerOn:
        _state = _state.copyWith(isSpeakerOn: true);
      case VoiceEventType.speakerOff:
        _state = _state.copyWith(isSpeakerOn: false);
      case VoiceEventType.permissionDenied:
        _state = _state.copyWith(
          phase: CallPhase.ended,
          error: 'The call needs microphone permission.',
        );
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
    _engine?.dispose();
    super.dispose();
  }
}
