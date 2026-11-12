import 'voice_engine.dart';

/// Translates an event relayed by web/plivo_bridge.js into the app's vocabulary.
///
/// Kept free of JS interop so it can be unit tested on any platform. Returns
/// null for events the call screen has no use for.
VoiceEvent? plivoEventToVoiceEvent(String type, Map<String, dynamic> detail) {
  switch (type) {
    case 'incoming':
      return VoiceEvent(
        VoiceEventType.incoming,
        from: plivoCallerToE164(detail['from'] as String? ?? ''),
      );
    case 'connecting':
      return const VoiceEvent(VoiceEventType.connecting);
    case 'ringing':
      return const VoiceEvent(VoiceEventType.ringing);
    case 'connected':
      return const VoiceEvent(VoiceEventType.connected);
    case 'ended':
      return VoiceEvent(
        VoiceEventType.ended,
        error: _describeFailure(detail['cause'] as String?),
      );
    case 'permissionDenied':
      return const VoiceEvent(VoiceEventType.permissionDenied);
    default:
      return null;
  }
}

/// Plivo dials bare digits; the app keeps numbers in E.164.
String plivoDigits(String number) => number.replaceAll(RegExp(r'[^0-9]'), '');

/// The SDK reports a caller as "14155551234", "+14155551234" or a SIP URI such
/// as "sip:14155551234@phone.plivo.com", depending on where the call came from.
String plivoCallerToE164(String raw) {
  var value = raw.trim();
  if (value.startsWith('sip:')) value = value.substring(4);
  final at = value.indexOf('@');
  if (at >= 0) value = value.substring(0, at);

  if (value.startsWith('+')) return value;
  return RegExp(r'^[0-9]{8,}$').hasMatch(value) ? '+$value' : value;
}

/// A call the far end simply declined or never answered is not an error worth
/// a red banner; anything else is shown as Plivo worded it.
String? _describeFailure(String? cause) {
  if (cause == null || cause.isEmpty) return null;

  return switch (cause.toLowerCase()) {
    'busy' => 'The line is busy.',
    'rejected' => 'The call was declined.',
    'no answer' || 'request timeout' => 'No answer.',
    'canceled' || 'terminated' || 'bye' => null,
    _ => 'The call failed: $cause',
  };
}
