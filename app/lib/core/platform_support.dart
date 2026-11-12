import 'package:flutter/foundation.dart';

import 'api_models.dart';

/// What this build can actually do, which depends on the provider as well as
/// the platform.
///
/// The Twilio Voice SDK has implementations for Android, iOS, macOS and web.
/// Plivo publishes no Flutter plugin, only a Browser SDK, so its in-app audio
/// exists on web alone. Everywhere else the app falls back to dial-out (the
/// provider rings your own handset, then bridges the call) rather than
/// pretending to offer in-app audio.
class PlatformSupport {
  const PlatformSupport._();

  static bool hasVoipSdk(String provider) {
    if (kIsWeb) return true;
    if (provider == TelephonyProvider.plivo) return false;

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
  static String voipUnavailableReason(String provider) => provider == TelephonyProvider.plivo
      ? 'Plivo offers in-app calling in the web app only, so calls cannot use '
          'this device\'s microphone. Dial-out will ring your own phone instead.'
      : 'The Twilio Voice SDK has no $name implementation, so calls cannot use '
          'this device\'s microphone. Dial-out will ring your own phone instead.';
}
