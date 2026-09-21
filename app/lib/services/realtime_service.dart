import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:signalr_netcore/signalr_client.dart';

import '../core/api_models.dart';

enum RealtimeStatus { disconnected, connecting, connected, reconnecting }

/// Receives inbound SMS and call notifications pushed from the backend.
///
/// Twilio delivers those to a webhook, which an app cannot host, so the backend
/// relays them over SignalR. A dropped connection is expected on mobile, so the
/// app also replays missed events over HTTP when it reconnects.
class RealtimeService extends ChangeNotifier {
  final String baseUrl;
  final Future<String?> Function() tokenProvider;

  HubConnection? _connection;
  RealtimeStatus _status = RealtimeStatus.disconnected;
  bool _disposed = false;

  final _messages = StreamController<InboundEvent>.broadcast();
  final _incomingCalls = StreamController<InboundEvent>.broadcast();
  final _statusUpdates = StreamController<({String sid, String status})>.broadcast();

  RealtimeService({required this.baseUrl, required this.tokenProvider});

  RealtimeStatus get status => _status;
  bool get isConnected => _status == RealtimeStatus.connected;

  Stream<InboundEvent> get onMessage => _messages.stream;
  Stream<InboundEvent> get onIncomingCall => _incomingCalls.stream;
  Stream<({String sid, String status})> get onStatusUpdate => _statusUpdates.stream;

  Future<void> connect() async {
    if (_connection != null) return;

    _setStatus(RealtimeStatus.connecting);

    // WebSockets cannot carry an Authorization header, so SignalR passes the
    // token as a query parameter; the backend accepts that on /hubs only.
    final connection = HubConnectionBuilder()
        .withUrl(
          '$baseUrl/hubs/realtime',
          options: HttpConnectionOptions(accessTokenFactory: () async {
            final token = await tokenProvider();
            return token ?? '';
          }),
        )
        .withAutomaticReconnect(retryDelays: [0, 2000, 5000, 10000, 20000])
        .build();

    connection.on('messageReceived', (args) => _emit(_messages, args));
    connection.on('incomingCall', (args) => _emit(_incomingCalls, args));
    connection.on('messageStatus', (args) => _emitStatus(args));
    connection.on('callStatus', (args) => _emitStatus(args));

    connection.onclose(({Exception? error}) {
      _setStatus(RealtimeStatus.disconnected);
    });
    connection.onreconnecting(({Exception? error}) {
      _setStatus(RealtimeStatus.reconnecting);
    });
    connection.onreconnected(({String? connectionId}) {
      _setStatus(RealtimeStatus.connected);
    });

    _connection = connection;

    try {
      await connection.start();
      _setStatus(RealtimeStatus.connected);
    } catch (error) {
      // Realtime is an optimisation: without it the app still works by polling,
      // so a failure here must not take the rest of the session down.
      debugPrint('Realtime connect failed: $error');
      _connection = null;
      _setStatus(RealtimeStatus.disconnected);
    }
  }

  Future<void> disconnect() async {
    final connection = _connection;
    _connection = null;
    if (connection != null) {
      try {
        await connection.stop();
      } catch (_) {
        // Already gone; nothing to clean up.
      }
    }
    _setStatus(RealtimeStatus.disconnected);
  }

  void _emit(StreamController<InboundEvent> sink, List<Object?>? args) {
    final payload = args?.firstOrNull;
    if (payload is! Map) return;
    try {
      sink.add(InboundEvent.fromJson(Map<String, dynamic>.from(payload)));
    } catch (error) {
      debugPrint('Malformed realtime payload: $error');
    }
  }

  void _emitStatus(List<Object?>? args) {
    final payload = args?.firstOrNull;
    if (payload is! Map) return;
    final sid = payload['sid'] as String?;
    final status = payload['status'] as String?;
    if (sid == null || status == null) return;
    _statusUpdates.add((sid: sid, status: status));
  }

  void _setStatus(RealtimeStatus status) {
    if (_status == status || _disposed) return;
    _status = status;
    notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    disconnect();
    _messages.close();
    _incomingCalls.close();
    _statusUpdates.close();
    super.dispose();
  }
}
