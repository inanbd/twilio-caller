import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:provider/provider.dart';

import 'package:twilio_caller/core/api_client.dart';
import 'package:twilio_caller/core/api_models.dart';
import 'package:twilio_caller/core/formatting.dart';
import 'package:twilio_caller/core/platform_support.dart';
import 'package:twilio_caller/services/voice/plivo_event.dart';
import 'package:twilio_caller/services/voice/voice_engine.dart';
import 'package:twilio_caller/services/voice_service.dart';
import 'package:twilio_caller/state/app_state.dart';
import 'package:twilio_caller/ui/screens/connect_screen.dart';

/// Records what [VoiceService] asks of a voice SDK, and lets a test play the
/// SDK's side by pushing events.
class FakeVoiceEngine implements VoiceEngine {
  final controller = StreamController<VoiceEvent>.broadcast(sync: true);
  final log = <String>[];
  VoiceToken? registeredWith;
  bool failNextCall = false;

  @override
  Stream<VoiceEvent> get events => controller.stream;

  @override
  bool get hasSystemCallUi => false;

  @override
  Future<void> register(VoiceToken credentials) async {
    registeredWith = credentials;
    log.add('register');
  }

  @override
  Future<void> unregister() async => log.add('unregister');

  @override
  Future<void> requestPermissions() async => log.add('permissions');

  @override
  Future<void> placeCall({required String from, required String to}) async {
    if (failNextCall) throw StateError('no microphone');
    log.add('call $from -> $to');
  }

  @override
  Future<void> answer() async => log.add('answer');

  @override
  Future<void> hangUp() async => log.add('hangup');

  @override
  Future<void> setMuted(bool muted) async => log.add('mute $muted');

  @override
  Future<void> setSpeaker(bool on) async => log.add('speaker $on');

  @override
  Future<void> sendDigits(String digits) async => log.add('digits $digits');

  @override
  void dispose() => log.add('dispose');
}

ApiClient apiReturning(Map<String, Object?> Function(http.Request) respond) => ApiClient(
      baseUrl: 'https://caller.test',
      httpClient: MockClient(
        (request) async => http.Response(jsonEncode(respond(request)), 200),
      ),
    );

const plivoCredentials = {
  'token': '',
  'identity': 'web_ab12cd',
  'expiresAt': '2099-01-01T00:00:00Z',
  'provider': 'plivo',
  'username': 'caller9f3k2a1234567890',
  'password': 'endpoint-password',
};

void main() {
  group('wire format', () {
    test('a session without a provider is a Twilio session', () {
      final session = Session.fromJson({
        'connectionId': 'c1',
        'accountSid': 'AC123',
        'identity': 'web_x',
      });

      expect(session.provider, TelephonyProvider.twilio);
      expect(session.providerLabel, 'Twilio');
    });

    test('a Plivo session and its voice credentials parse', () {
      final session = Session.fromJson({
        'connectionId': 'c1',
        'accountSid': 'MA123',
        'identity': 'web_x',
        'provider': 'plivo',
      });
      final credentials = VoiceToken.fromJson(plivoCredentials);

      expect(session.providerLabel, 'Plivo');
      expect(credentials.provider, TelephonyProvider.plivo);
      expect(credentials.username, 'caller9f3k2a1234567890');
      expect(credentials.password, 'endpoint-password');
      // Endpoint credentials do not expire, so nothing should try to refresh them.
      expect(credentials.isExpiring, isFalse);
    });

    test('connecting to Plivo sends the Auth ID and token, and no API key', () async {
      late Map<String, dynamic> sent;
      final api = apiReturning((request) {
        sent = jsonDecode(request.body) as Map<String, dynamic>;
        return {
          'sessionToken': 'session',
          'expiresAt': '2099-01-01T00:00:00Z',
          'connectionId': 'c1',
          'accountSid': 'MA123',
          'identity': 'web_x',
          'provider': 'plivo',
        };
      });

      final result = await api.connect(
        provider: TelephonyProvider.plivo,
        accountSid: ' MA123 ',
        authToken: ' secret ',
        platform: 'web',
        supportsVoip: true,
      );

      expect(sent['provider'], 'plivo');
      expect(sent['accountSid'], 'MA123');
      expect(sent['authToken'], 'secret');
      expect(sent['apiKeySid'], isNull);
      expect(sent['apiKeySecret'], isNull);
      expect(result.session.provider, TelephonyProvider.plivo);
    });

    test('a message Plivo rejected counts as failed', () {
      final message = SmsMessage.fromJson({'sid': 'm1', 'status': 'rejected'});
      expect(message.hasFailed, isTrue);
    });
  });

  group('PlatformSupport', () {
    tearDown(() => debugDefaultTargetPlatformOverride = null);

    test('Plivo has no in-app audio on native platforms, where Twilio does', () {
      debugDefaultTargetPlatformOverride = TargetPlatform.android;

      expect(PlatformSupport.hasVoipSdk(TelephonyProvider.twilio), isTrue);
      expect(PlatformSupport.hasVoipSdk(TelephonyProvider.plivo), isFalse);
      expect(
        PlatformSupport.voipUnavailableReason(TelephonyProvider.plivo),
        contains('web app only'),
      );
    });
  });

  group('Plivo SDK events', () {
    test('an incoming call carries the caller in E.164', () {
      final event = plivoEventToVoiceEvent('incoming', {'from': '14155559999'});

      expect(event!.type, VoiceEventType.incoming);
      expect(event.from, '+14155559999');
    });

    test('callers arrive in several shapes', () {
      expect(plivoCallerToE164('+14155559999'), '+14155559999');
      expect(plivoCallerToE164('sip:14155559999@phone.plivo.com'), '+14155559999');
      expect(plivoCallerToE164('sip:alice123@phone.plivo.com'), 'alice123');
      expect(plivoCallerToE164(''), '');
    });

    test('a normal hang-up is not an error, a failure is', () {
      expect(plivoEventToVoiceEvent('ended', {})!.error, isNull);
      expect(plivoEventToVoiceEvent('ended', {'cause': 'Canceled'})!.error, isNull);
      expect(plivoEventToVoiceEvent('ended', {'cause': 'Busy'})!.error, 'The line is busy.');
      expect(
        plivoEventToVoiceEvent('ended', {'cause': 'Network Error'})!.error,
        contains('Network Error'),
      );
    });

    test('unknown events are ignored', () {
      expect(plivoEventToVoiceEvent('onSomethingNew', {}), isNull);
    });

    test('numbers go to Plivo as bare digits', () {
      expect(plivoDigits('+1 (415) 555-0000'), '14155550000');
    });
  });

  group('VoiceService', () {
    late FakeVoiceEngine engine;
    late List<String> created;
    late VoiceService voice;

    setUp(() {
      engine = FakeVoiceEngine();
      created = [];
      voice = VoiceService(
        api: apiReturning((_) => plivoCredentials),
        supportsVoip: true,
        engineFactory: (provider) {
          created.add(provider);
          return engine;
        },
      );
    });

    test('registers the engine of the connected provider with backend credentials',
        () async {
      await voice.useProvider(TelephonyProvider.plivo);
      await voice.register();

      expect(created, [TelephonyProvider.plivo]);
      expect(engine.registeredWith!.username, 'caller9f3k2a1234567890');
      expect(voice.isRegistered, isTrue);
    });

    test('an outbound call walks connecting, ringing, active, ended', () async {
      await voice.useProvider(TelephonyProvider.plivo);
      await voice.placeCall(from: '+14155550000', to: '+14155557777');

      expect(engine.log, contains('call +14155550000 -> +14155557777'));
      expect(voice.state.phase, CallPhase.connecting);
      expect(voice.state.peerNumber, '+14155557777');

      engine.controller.add(const VoiceEvent(VoiceEventType.ringing));
      expect(voice.state.phase, CallPhase.ringing);

      engine.controller.add(const VoiceEvent(VoiceEventType.connected));
      expect(voice.state.phase, CallPhase.active);
      expect(voice.state.connectedAt, isNotNull);

      engine.controller.add(const VoiceEvent(VoiceEventType.ended));
      expect(voice.state.phase, CallPhase.ended);
      expect(voice.state.isBusy, isFalse);
    });

    test('a call that fails to start does not strand the call screen', () async {
      await voice.register();
      engine.failNextCall = true;

      await expectLater(
        voice.placeCall(from: '+14155550000', to: '+14155557777'),
        throwsStateError,
      );
      expect(voice.state.isBusy, isFalse);
    });

    test('an incoming call rings, and a failure is surfaced when it ends', () async {
      await voice.register();

      engine.controller.add(
        const VoiceEvent(VoiceEventType.incoming, from: '+14155559999'),
      );
      expect(voice.state.phase, CallPhase.ringing);
      expect(voice.state.isInbound, isTrue);
      expect(voice.state.peerNumber, '+14155559999');

      await voice.answer();
      expect(engine.log, containsAllInOrder(['permissions', 'answer']));

      engine.controller.add(
        const VoiceEvent(VoiceEventType.ended, error: 'The call failed: Network Error'),
      );
      expect(voice.state.error, contains('Network Error'));
    });

    test('a late "ended" after the screen was dismissed changes nothing', () async {
      await voice.register();
      var notified = 0;
      voice.addListener(() => notified++);

      engine.controller.add(const VoiceEvent(VoiceEventType.ended));

      expect(voice.state.phase, CallPhase.idle);
      expect(notified, 0);
    });

    test('switching provider signs out of the old SDK and builds the new one',
        () async {
      await voice.register();
      await voice.useProvider(TelephonyProvider.plivo);
      await voice.register();

      expect(engine.log, containsAllInOrder(['register', 'unregister', 'dispose']));
      expect(created, [TelephonyProvider.twilio, TelephonyProvider.plivo]);
    });

    test('without a voice SDK nothing is registered and calls are refused', () async {
      final dialOutOnly = VoiceService(
        api: apiReturning((_) => plivoCredentials),
        supportsVoip: false,
        engineFactory: (_) => fail('no engine should be built'),
      );
      await dialOutOnly.useProvider(TelephonyProvider.plivo);

      await dialOutOnly.register();
      expect(dialOutOnly.isRegistered, isFalse);
      await expectLater(
        dialOutOnly.placeCall(from: '+14155550000', to: '+14155557777'),
        throwsStateError,
      );
    });
  });

  group('Format.phone', () {
    test('unwraps a Plivo endpoint', () {
      expect(Format.phone('sip:caller9f3k21234@phone.plivo.com'), 'caller9f3k21234');
    });
  });

  group('ConnectScreen', () {
    Future<void> pump(WidgetTester tester) => tester.pumpWidget(
          ChangeNotifierProvider(
            create: (_) => AppState(),
            child: const MaterialApp(home: ConnectScreen()),
          ),
        );

    testWidgets('asks for a Twilio API key by default', (tester) async {
      await pump(tester);

      expect(find.text('Account SID'), findsOneWidget);
      expect(find.text('API Key SID'), findsOneWidget);
      expect(find.text('API Key Secret'), findsOneWidget);
      expect(find.text('Auth ID'), findsNothing);
    });

    testWidgets('choosing Plivo swaps in its two credentials and validates them',
        (tester) async {
      await pump(tester);

      await tester.tap(find.text('Plivo'));
      await tester.pumpAndSettle();

      expect(find.text('Auth ID'), findsOneWidget);
      expect(find.text('Auth Token'), findsOneWidget);
      expect(find.text('API Key SID'), findsNothing);

      await tester.enterText(
        find.widgetWithText(TextFormField, 'Backend URL'),
        'https://caller.test',
      );
      await tester.enterText(find.widgetWithText(TextFormField, 'Auth ID'), 'AC-not-plivo');
      await tester.ensureVisible(find.text('Connect'));
      await tester.tap(find.text('Connect'));
      await tester.pumpAndSettle();

      expect(find.textContaining('starts with "MA"'), findsOneWidget);
      expect(find.textContaining('Enter the Auth Token'), findsOneWidget);
    });
  });
}
