import 'package:flutter/foundation.dart';

/// What this build can actually do.
///
/// The Twilio Voice SDK has implementations for Android, iOS, macOS and web.
/// On Linux and Windows there is none, so those builds fall back to dial-out
/// (Twilio rings your own handset, then bridges the call) rather than pretending
/// to offer in-app audio.
class PlatformSupport {
  const PlatformSupport._();

  static bool get hasVoipSdk {
    if (kIsWeb) return true;
    return defaultTargetPlatform == TargetPlatform.android ||
        defaultTargetPlatform == TargetPlatform.iOS ||
        defaultTargetPlatform == TargetPlatform.macOS;
  }

  /// Only mobile has system call UI (CallKit / ConnectionService) and the
  /// background permissions that go with it.
  static bool get hasSystemCallUi =>
      !kIsWeb &&
      (defaultTargetPlatform == TargetPlatform.android ||
          defaultTargetPlatform == TargetPlatform.iOS);

  static String get name {
    if (kIsWeb) return 'web';
    return defaultTargetPlatform.name;
  }

  /// Why in-app calling is unavailable, for the UI to explain rather than just
  /// disabling a button.
  static String get voipUnavailableReason =>
      'The Twilio Voice SDK has no $name implementation, so calls cannot use '
      'this device\'s microphone. Dial-out will ring your own phone instead.';
}
