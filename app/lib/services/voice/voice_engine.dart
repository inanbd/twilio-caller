import '../../core/api_models.dart';

/// What a voice SDK can tell the app about the call in progress.
enum VoiceEventType {
  incoming,
  connecting,
  ringing,
  connected,
  reconnecting,
  reconnected,
  ended,
  muted,
  unmuted,
  speakerOn,
  speakerOff,
  permissionDenied,
}

class VoiceEvent {
  final VoiceEventType type;

  /// For [VoiceEventType.incoming]: who is calling, and which number they dialled
  /// when the SDK knows it.
  final String? from;
  final String? to;

  /// For [VoiceEventType.ended]: why, when it was not a normal hang-up.
  final String? error;

  const VoiceEvent(this.type, {this.from, this.to, this.error});
}

/// One provider's in-app audio SDK, reduced to what the call screen needs.
///
/// [VoiceService] owns the call state and talks to whichever engine matches the
/// connected account, so nothing above it knows which provider is in use.
abstract class VoiceEngine {
  Stream<VoiceEvent> get events;

  /// Whether calls show in the system's own call UI (CallKit / ConnectionService),
  /// which also means the system, not the app, routes audio and holds the mic.
  bool get hasSystemCallUi;

  /// Brings the SDK online with credentials issued by the backend.
  Future<void> register(VoiceToken credentials);

  Future<void> unregister();

  /// Asks for whatever a call needs up front, so no prompt lands mid-ring.
  Future<void> requestPermissions();

  Future<void> placeCall({required String from, required String to});

  Future<void> answer();
  Future<void> hangUp();
  Future<void> setMuted(bool muted);
  Future<void> setSpeaker(bool on);
  Future<void> sendDigits(String digits);

  void dispose();
}
