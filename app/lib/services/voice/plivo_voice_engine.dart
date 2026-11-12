import 'plivo_voice_engine_stub.dart'
    if (dart.library.js_interop) 'plivo_voice_engine_web.dart' as impl;
import 'voice_engine.dart';

/// Plivo's in-app audio. Plivo publishes no Flutter plugin, only a Browser SDK,
/// so this exists on web and nowhere else; other platforms use dial-out.
VoiceEngine createPlivoVoiceEngine() => impl.createPlivoVoiceEngine();
