/// Mirrors of the backend's DTOs. Kept deliberately dumb: parsing lives here so
/// that a change to the wire format shows up as a compile error in one place.
library;


class Session {
  final String connectionId;
  final String accountSid;
  final String friendlyName;
  final String identity;
  final bool voiceReady;
  final String? twimlAppSid;
  final String? fallbackForwardNumber;

  const Session({
    required this.connectionId,
    required this.accountSid,
    required this.friendlyName,
    required this.identity,
    required this.voiceReady,
    this.twimlAppSid,
    this.fallbackForwardNumber,
  });

  factory Session.fromJson(Map<String, dynamic> json) => Session(
        connectionId: json['connectionId'] as String,
        accountSid: json['accountSid'] as String,
        friendlyName: json['friendlyName'] as String? ?? '',
        identity: json['identity'] as String,
        voiceReady: json['voiceReady'] as bool? ?? false,
        twimlAppSid: json['twimlAppSid'] as String?,
        fallbackForwardNumber: json['fallbackForwardNumber'] as String?,
      );
}

class ConnectResult {
  final String sessionToken;
  final DateTime expiresAt;
  final Session session;

  const ConnectResult({
    required this.sessionToken,
    required this.expiresAt,
    required this.session,
  });

  factory ConnectResult.fromJson(Map<String, dynamic> json) => ConnectResult(
        sessionToken: json['sessionToken'] as String,
        expiresAt: DateTime.parse(json['expiresAt'] as String),
        session: Session(
          connectionId: json['connectionId'] as String,
          accountSid: json['accountSid'] as String,
          friendlyName: json['friendlyName'] as String? ?? '',
          identity: json['identity'] as String,
          voiceReady: json['voiceReady'] as bool? ?? false,
        ),
      );
}

class TwilioNumber {
  final String sid;
  final String phoneNumber;
  final String friendlyName;
  final bool smsEnabled;
  final bool voiceEnabled;
  final bool mmsEnabled;
  final bool wiredToThisBackend;

  const TwilioNumber({
    required this.sid,
    required this.phoneNumber,
    required this.friendlyName,
    required this.smsEnabled,
    required this.voiceEnabled,
    required this.mmsEnabled,
    required this.wiredToThisBackend,
  });

  factory TwilioNumber.fromJson(Map<String, dynamic> json) => TwilioNumber(
        sid: json['sid'] as String,
        phoneNumber: json['phoneNumber'] as String? ?? '',
        friendlyName: json['friendlyName'] as String? ?? '',
        smsEnabled: json['smsEnabled'] as bool? ?? false,
        voiceEnabled: json['voiceEnabled'] as bool? ?? false,
        mmsEnabled: json['mmsEnabled'] as bool? ?? false,
        wiredToThisBackend: json['wiredToThisBackend'] as bool? ?? false,
      );

  String get label =>
      friendlyName.isEmpty || friendlyName == phoneNumber ? phoneNumber : friendlyName;
}

class ProvisionResult {
  final String twimlAppSid;
  final List<String> updatedNumberSids;
  final List<String> failures;

  const ProvisionResult({
    required this.twimlAppSid,
    required this.updatedNumberSids,
    required this.failures,
  });

  factory ProvisionResult.fromJson(Map<String, dynamic> json) => ProvisionResult(
        twimlAppSid: json['twimlAppSid'] as String? ?? '',
        updatedNumberSids:
            (json['updatedNumberSids'] as List<dynamic>? ?? []).cast<String>(),
        failures: (json['failures'] as List<dynamic>? ?? []).cast<String>(),
      );
}

enum MessageDirection { inbound, outbound, unknown }

class SmsMessage {
  final String sid;
  final String from;
  final String to;
  final String? body;
  final MessageDirection direction;
  final String status;
  final int numMedia;
  final DateTime? sentAt;
  final String? errorMessage;

  const SmsMessage({
    required this.sid,
    required this.from,
    required this.to,
    required this.body,
    required this.direction,
    required this.status,
    required this.numMedia,
    required this.sentAt,
    this.errorMessage,
  });

  factory SmsMessage.fromJson(Map<String, dynamic> json) => SmsMessage(
        sid: json['sid'] as String,
        from: json['from'] as String? ?? '',
        to: json['to'] as String? ?? '',
        body: json['body'] as String?,
        direction: _parseDirection(json['direction'] as String?),
        status: json['status'] as String? ?? 'unknown',
        numMedia: json['numMedia'] as int? ?? 0,
        sentAt: json['sentAt'] == null
            ? null
            : DateTime.parse(json['sentAt'] as String).toLocal(),
        errorMessage: json['errorMessage'] as String?,
      );

  // Twilio spells these "inbound", "outbound-api", "outbound-reply", ...
  static MessageDirection _parseDirection(String? raw) {
    if (raw == null) return MessageDirection.unknown;
    if (raw.startsWith('inbound')) return MessageDirection.inbound;
    if (raw.startsWith('outbound')) return MessageDirection.outbound;
    return MessageDirection.unknown;
  }

  bool get isOutbound => direction == MessageDirection.outbound;

  /// True while Twilio has not yet confirmed the carrier accepted the message.
  bool get isPending => status == 'queued' || status == 'accepted' || status == 'sending';

  bool get hasFailed => status == 'failed' || status == 'undelivered';
}

class Conversation {
  final String ownedNumber;
  final String peerNumber;
  final String? lastBody;
  final DateTime? lastAt;
  final int messageCount;

  const Conversation({
    required this.ownedNumber,
    required this.peerNumber,
    required this.lastBody,
    required this.lastAt,
    required this.messageCount,
  });

  factory Conversation.fromJson(Map<String, dynamic> json) => Conversation(
        ownedNumber: json['ownedNumber'] as String? ?? '',
        peerNumber: json['peerNumber'] as String? ?? '',
        lastBody: json['lastBody'] as String?,
        lastAt: json['lastAt'] == null
            ? null
            : DateTime.parse(json['lastAt'] as String).toLocal(),
        messageCount: json['messageCount'] as int? ?? 0,
      );
}

class CallRecord {
  final String sid;
  final String from;
  final String to;
  final String direction;
  final String status;
  final int? durationSeconds;
  final DateTime? startedAt;

  const CallRecord({
    required this.sid,
    required this.from,
    required this.to,
    required this.direction,
    required this.status,
    required this.durationSeconds,
    required this.startedAt,
  });

  factory CallRecord.fromJson(Map<String, dynamic> json) => CallRecord(
        sid: json['sid'] as String,
        from: json['from'] as String? ?? '',
        to: json['to'] as String? ?? '',
        direction: json['direction'] as String? ?? 'unknown',
        status: json['status'] as String? ?? 'unknown',
        durationSeconds: json['durationSeconds'] as int?,
        startedAt: json['startedAt'] == null
            ? null
            : DateTime.parse(json['startedAt'] as String).toLocal(),
      );

  bool get isInbound => direction.startsWith('inbound');
  bool get wasMissed => isInbound && (status == 'no-answer' || status == 'busy');
}

class VoiceToken {
  final String token;
  final String identity;
  final DateTime expiresAt;

  const VoiceToken({
    required this.token,
    required this.identity,
    required this.expiresAt,
  });

  factory VoiceToken.fromJson(Map<String, dynamic> json) => VoiceToken(
        token: json['token'] as String,
        identity: json['identity'] as String,
        expiresAt: DateTime.parse(json['expiresAt'] as String),
      );

  bool get isExpiring =>
      expiresAt.difference(DateTime.now().toUtc()).inMinutes < 5;
}

/// An inbound SMS or call relayed from a Twilio webhook.
class InboundEvent {
  final int id;
  final String kind;
  final String sid;
  final String from;
  final String to;
  final String? body;
  final String? status;
  final int numMedia;
  final DateTime receivedAt;

  const InboundEvent({
    required this.id,
    required this.kind,
    required this.sid,
    required this.from,
    required this.to,
    required this.body,
    required this.status,
    required this.numMedia,
    required this.receivedAt,
  });

  factory InboundEvent.fromJson(Map<String, dynamic> json) => InboundEvent(
        id: (json['id'] as num?)?.toInt() ?? 0,
        kind: json['kind'] as String? ?? '',
        sid: json['sid'] as String? ?? '',
        from: json['from'] as String? ?? '',
        to: json['to'] as String? ?? '',
        body: json['body'] as String?,
        status: json['status'] as String?,
        numMedia: (json['numMedia'] as num?)?.toInt() ?? 0,
        receivedAt: json['receivedAt'] == null
            ? DateTime.now()
            : DateTime.parse(json['receivedAt'] as String).toLocal(),
      );

  bool get isMessage => kind == 'message';
}
