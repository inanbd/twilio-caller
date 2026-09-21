import 'dart:convert';

import 'package:http/http.dart' as http;

import 'api_models.dart';

/// A failure the UI can show verbatim. The backend forwards Twilio's own error
/// text, which is almost always more useful than anything we could invent.
class ApiException implements Exception {
  final int statusCode;
  final String code;
  final String message;

  const ApiException(this.statusCode, this.code, this.message);

  bool get isUnauthorized => statusCode == 401;

  @override
  String toString() => message;
}

/// Talks to the companion backend. Holds the session token but never the Twilio
/// API key secret, which stays on the server after the one-time connect call.
class ApiClient {
  final String baseUrl;
  final http.Client _http;

  /// Set after connect, and cleared on sign-out or a 401.
  String? sessionToken;

  ApiClient({required this.baseUrl, http.Client? httpClient})
      : _http = httpClient ?? http.Client();

  Map<String, String> get _headers => {
        'Content-Type': 'application/json',
        if (sessionToken != null) 'Authorization': 'Bearer $sessionToken',
      };

  // ---------- auth ----------

  Future<ConnectResult> connect({
    required String accountSid,
    required String apiKeySid,
    required String apiKeySecret,
    String? authToken,
    required String platform,
    required bool supportsVoip,
  }) async {
    final json = await _post('/api/auth/connect', {
      'accountSid': accountSid.trim(),
      'apiKeySid': apiKeySid.trim(),
      'apiKeySecret': apiKeySecret.trim(),
      'authToken': authToken?.trim().isEmpty ?? true ? null : authToken!.trim(),
      'platform': platform,
      'supportsVoip': supportsVoip,
    }, authenticated: false);

    final result = ConnectResult.fromJson(json as Map<String, dynamic>);
    sessionToken = result.sessionToken;
    return result;
  }

  Future<Session> session() async =>
      Session.fromJson(await _get('/api/auth/session') as Map<String, dynamic>);

  // ---------- numbers ----------

  Future<List<TwilioNumber>> numbers() async {
    final json = await _get('/api/numbers') as List<dynamic>;
    return json
        .map((e) => TwilioNumber.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<ProvisionResult> provision({
    required List<String> numberSids,
    String? fallbackForwardNumber,
  }) async {
    final json = await _post('/api/numbers/provision', {
      'phoneNumberSids': numberSids,
      'fallbackForwardNumber': fallbackForwardNumber,
    });
    return ProvisionResult.fromJson(json as Map<String, dynamic>);
  }

  // ---------- messages ----------

  Future<List<Conversation>> conversations(String number, {int limit = 200}) async {
    final json = await _get(
      '/api/messages/conversations',
      query: {'number': number, 'limit': '$limit'},
    ) as List<dynamic>;
    return json
        .map((e) => Conversation.fromJson(e as Map<String, dynamic>))
        .toList();
  }

  Future<List<SmsMessage>> messages(
    String number, {
    String? peer,
    int limit = 100,
  }) async {
    final json = await _get('/api/messages', query: {
      'number': number,
      'peer': ?peer,
      'limit': '$limit',
    }) as List<dynamic>;
    return json.map((e) => SmsMessage.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<SmsMessage> sendMessage({
    required String from,
    required String to,
    required String body,
  }) async {
    final json = await _post('/api/messages', {
      'from': from,
      'to': to,
      'body': body,
    });
    return SmsMessage.fromJson(json as Map<String, dynamic>);
  }

  // ---------- calls ----------

  Future<List<CallRecord>> calls({String? number, int limit = 50}) async {
    final json = await _get('/api/calls', query: {
      'number': ?number,
      'limit': '$limit',
    }) as List<dynamic>;
    return json.map((e) => CallRecord.fromJson(e as Map<String, dynamic>)).toList();
  }

  Future<VoiceToken> voiceToken() async =>
      VoiceToken.fromJson(await _get('/api/voice/token') as Map<String, dynamic>);

  Future<CallRecord> dialOut({
    required String from,
    required String to,
    required String bridgeTo,
  }) async {
    final json = await _post('/api/calls/dial-out', {
      'from': from,
      'to': to,
      'bridgeTo': bridgeTo,
    });
    return CallRecord.fromJson(json as Map<String, dynamic>);
  }

  // ---------- event replay ----------

  Future<List<InboundEvent>> events({int? since, int limit = 200}) async {
    final json = await _get('/api/events', query: {
      if (since != null) 'since': '$since',
      'limit': '$limit',
    }) as List<dynamic>;
    return json.map((e) => InboundEvent.fromJson(e as Map<String, dynamic>)).toList();
  }

  // ---------- plumbing ----------

  Future<dynamic> _get(String path, {Map<String, String>? query}) async {
    final uri = Uri.parse('$baseUrl$path')
        .replace(queryParameters: query?.isEmpty ?? true ? null : query);
    return _decode(await _http.get(uri, headers: _headers));
  }

  Future<dynamic> _post(
    String path,
    Map<String, dynamic> body, {
    bool authenticated = true,
  }) async {
    final response = await _http.post(
      Uri.parse('$baseUrl$path'),
      headers: authenticated ? _headers : {'Content-Type': 'application/json'},
      body: jsonEncode(body),
    );
    return _decode(response);
  }

  dynamic _decode(http.Response response) {
    if (response.statusCode >= 200 && response.statusCode < 300) {
      if (response.body.isEmpty) return null;
      return jsonDecode(response.body);
    }

    String code = 'http_${response.statusCode}';
    String message = _defaultMessage(response.statusCode);

    try {
      final json = jsonDecode(response.body);
      if (json is Map<String, dynamic>) {
        code = json['error'] as String? ?? code;
        message = (json['detail'] as String?) ??
            (json['error'] as String?) ??
            message;
      }
    } on FormatException {
      // A non-JSON error body (a proxy's HTML page, say) leaves the default text.
    }

    throw ApiException(response.statusCode, code, message);
  }

  String _defaultMessage(int status) => switch (status) {
        401 => 'Your session has expired. Sign in again.',
        403 => 'The backend refused that request.',
        404 => 'The backend has no such endpoint. Check the URL and its version.',
        >= 500 => 'The backend failed to handle that request.',
        _ => 'Request failed with status $status.',
      };

  void close() => _http.close();
}
