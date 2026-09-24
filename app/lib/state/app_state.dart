import 'dart:async';

import 'package:flutter/foundation.dart';

import '../core/api_client.dart';
import '../core/api_models.dart';
import '../core/config.dart';
import '../core/platform_support.dart';
import '../core/secure_store.dart';
import '../services/realtime_service.dart';
import '../services/voice_service.dart';

enum AppPhase {
  starting,

  /// No backend URL or no signed-in user: show login/registration.
  needsAuth,

  /// Signed in, but the account has no Twilio account attached yet.
  needsTwilio,

  ready,
}

/// The single source of truth the UI listens to.
///
/// Twilio is the system of record for messages and calls, so this holds a cache
/// rather than a database: it is refreshed from the backend and patched in place
/// by realtime events.
class AppState extends ChangeNotifier {
  final SecureStore _store;

  AppPhase _phase = AppPhase.starting;
  String? _backendUrl;
  ApiClient? _api;
  RealtimeService? _realtime;
  VoiceService? _voice;
  Session? _session;

  List<TwilioNumber> _numbers = [];
  String? _selectedNumber;
  List<Conversation> _conversations = [];
  List<CallRecord> _calls = [];
  final Map<String, List<SmsMessage>> _threads = {};

  bool _loadingNumbers = false;
  bool _loadingConversations = false;
  bool _loadingCalls = false;
  String? _lastError;
  int _lastEventId = 0;
  Timer? _tokenRefreshTimer;

  final _unreadPeers = <String>{};

  AppState({SecureStore? store}) : _store = store ?? SecureStore();

  // ---------- exposed state ----------

  AppPhase get phase => _phase;
  String? get backendUrl => _backendUrl;
  Session? get session => _session;
  ApiClient? get api => _api;
  RealtimeService? get realtime => _realtime;
  VoiceService? get voice => _voice;

  List<TwilioNumber> get numbers => List.unmodifiable(_numbers);
  List<Conversation> get conversations => List.unmodifiable(_conversations);
  List<CallRecord> get calls => List.unmodifiable(_calls);
  String? get selectedNumber => _selectedNumber;
  bool get loadingNumbers => _loadingNumbers;
  bool get loadingConversations => _loadingConversations;
  bool get loadingCalls => _loadingCalls;
  String? get lastError => _lastError;
  Set<String> get unreadPeers => Set.unmodifiable(_unreadPeers);

  TwilioNumber? get selectedNumberDetails {
    if (_selectedNumber == null) return null;
    for (final number in _numbers) {
      if (number.phoneNumber == _selectedNumber) return number;
    }
    return null;
  }

  /// True when at least one number actually routes to this backend. Until then
  /// nothing inbound can arrive, which the UI needs to say out loud.
  bool get hasProvisionedNumber =>
      _numbers.any((n) => n.wiredToThisBackend);

  List<SmsMessage> threadFor(String peer) =>
      List.unmodifiable(_threads[peer] ?? const []);

  // ---------- lifecycle ----------

  Future<void> boot() async {
    _backendUrl = await BackendConfig.load();
    if (_backendUrl == null) {
      _setPhase(AppPhase.needsAuth);
      return;
    }

    final token = await _store.readToken();
    if (token == null) {
      _buildClients(_backendUrl!);
      _setPhase(AppPhase.needsAuth);
      return;
    }

    _buildClients(_backendUrl!);
    _api!.sessionToken = token;

    try {
      _session = await _api!.session();
      await _afterAuthenticated();
    } on ApiException catch (error) {
      // An expired or rejected token just means signing in again.
      if (!error.isUnauthorized) _lastError = error.message;
      await _store.clear();
      _api!.sessionToken = null;
      _setPhase(AppPhase.needsAuth);
    } catch (error) {
      _lastError = 'Could not reach the backend at $_backendUrl. ($error)';
      _setPhase(AppPhase.needsAuth);
    }
  }

  Future<void> setBackendUrl(String url) async {
    final normalised = BackendConfig.normalise(url);
    await BackendConfig.save(normalised);
    _backendUrl = normalised;
    _buildClients(normalised);
    _setPhase(AppPhase.needsAuth);
  }

  Future<void> register({
    required String email,
    required String password,
    String? displayName,
  }) async {
    final api = _requireApi();
    final result = await api.register(
      email: email,
      password: password,
      displayName: displayName,
      platform: PlatformSupport.name,
      supportsVoip: PlatformSupport.hasVoipSdk,
    );
    await _completeSignIn(result);
  }

  Future<void> login({required String email, required String password}) async {
    final api = _requireApi();
    final result = await api.login(
      email: email,
      password: password,
      platform: PlatformSupport.name,
      supportsVoip: PlatformSupport.hasVoipSdk,
    );
    await _completeSignIn(result);
  }

  Future<void> _completeSignIn(AuthResult result) async {
    await _store.writeToken(result.sessionToken);
    _session = await _api!.session();
    _lastError = null;
    await _afterAuthenticated();
  }

  /// A signed-in user without a Twilio account goes to the connect step; with
  /// one, straight to the app.
  Future<void> _afterAuthenticated() async {
    if (_session?.hasTwilio ?? false) {
      await _onSignedIn();
    } else {
      _setPhase(AppPhase.needsTwilio);
    }
  }

  Future<void> connectTwilio({
    required String accountSid,
    required String apiKeySid,
    required String apiKeySecret,
    String? authToken,
  }) async {
    final api = _requireApi();
    await api.connectTwilio(
      accountSid: accountSid,
      apiKeySid: apiKeySid,
      apiKeySecret: apiKeySecret,
      authToken: authToken,
    );

    _session = await api.session();
    _lastError = null;
    await _onSignedIn();
  }

  Future<void> disconnectTwilio() async {
    final api = _requireApi();
    await api.disconnectTwilio();

    await _voice?.unregister();
    await _realtime?.disconnect();
    _numbers = [];
    _conversations = [];
    _calls = [];
    _threads.clear();
    _unreadPeers.clear();
    _selectedNumber = null;
    _lastEventId = 0;

    _session = await api.session();
    _setPhase(AppPhase.needsTwilio);
  }

  Future<void> changePassword({
    required String currentPassword,
    required String newPassword,
  }) async {
    final api = _requireApi();
    // The client installs the fresh token; persist it so a restart stays in.
    final result = await api.changePassword(
      currentPassword: currentPassword,
      newPassword: newPassword,
    );
    await _store.writeToken(result.sessionToken);
  }

  ApiClient _requireApi() {
    final api = _api;
    if (api == null) throw StateError('Set the backend URL first.');
    return api;
  }

  Future<void> signOut() async {
    _tokenRefreshTimer?.cancel();
    _tokenRefreshTimer = null;
    await _voice?.unregister();
    await _realtime?.disconnect();
    await _store.clear();

    _api?.sessionToken = null;
    _session = null;
    _numbers = [];
    _conversations = [];
    _calls = [];
    _threads.clear();
    _unreadPeers.clear();
    _selectedNumber = null;
    _lastEventId = 0;

    _setPhase(AppPhase.needsAuth);
  }

  void _buildClients(String baseUrl) {
    _api?.close();
    _api = ApiClient(baseUrl: baseUrl);
    _voice = VoiceService(api: _api!);
    _realtime = RealtimeService(
      baseUrl: baseUrl,
      tokenProvider: () async => _api?.sessionToken,
    );

    _realtime!.onMessage.listen(_onInboundMessage);
    _realtime!.onStatusUpdate.listen(_onStatusUpdate);
  }

  Future<void> _onSignedIn() async {
    _setPhase(AppPhase.ready);

    await refreshNumbers();
    unawaited(_realtime?.connect());

    if (PlatformSupport.hasVoipSdk) {
      try {
        await _voice?.register();
        // Access tokens expire; refresh on a timer so an inbound call always rings.
        _tokenRefreshTimer?.cancel();
        _tokenRefreshTimer = Timer.periodic(
          const Duration(minutes: 5),
          (_) => _voice?.refreshTokenIfNeeded(),
        );
      } catch (error) {
        _lastError = 'In-app calling is unavailable: $error';
        notifyListeners();
      }
    }
  }

  // ---------- data ----------

  Future<void> refreshNumbers() async {
    final api = _api;
    if (api == null) return;

    _loadingNumbers = true;
    notifyListeners();

    try {
      _numbers = await api.numbers();
      _lastError = null;

      // Prefer a number that is actually wired up; a number that is not cannot
      // receive anything, so defaulting to it would look broken.
      if (_selectedNumber == null ||
          !_numbers.any((n) => n.phoneNumber == _selectedNumber)) {
        final wired = _numbers.where((n) => n.wiredToThisBackend);
        _selectedNumber = wired.isNotEmpty
            ? wired.first.phoneNumber
            : (_numbers.isNotEmpty ? _numbers.first.phoneNumber : null);
      }
    } on ApiException catch (error) {
      _lastError = error.message;
    } finally {
      _loadingNumbers = false;
      notifyListeners();
    }

    if (_selectedNumber != null) {
      await Future.wait([refreshConversations(), refreshCalls()]);
    }
  }

  Future<void> selectNumber(String phoneNumber) async {
    if (_selectedNumber == phoneNumber) return;
    _selectedNumber = phoneNumber;
    _threads.clear();
    _unreadPeers.clear();
    notifyListeners();
    await Future.wait([refreshConversations(), refreshCalls()]);
  }

  Future<ProvisionResult> provision(
    List<String> numberSids, {
    String? fallbackForwardNumber,
  }) async {
    final result = await _api!.provision(
      numberSids: numberSids,
      fallbackForwardNumber: fallbackForwardNumber,
    );
    await refreshNumbers();
    // Provisioning creates the TwiML app, without which no token can be issued.
    if (PlatformSupport.hasVoipSdk) {
      try {
        await _voice?.register();
      } catch (error) {
        debugPrint('Voice registration after provisioning failed: $error');
      }
    }
    return result;
  }

  Future<void> refreshConversations() async {
    final number = _selectedNumber;
    if (number == null || _api == null) return;

    _loadingConversations = true;
    notifyListeners();

    try {
      _conversations = await _api!.conversations(number);
      _lastError = null;
    } on ApiException catch (error) {
      _lastError = error.message;
    } finally {
      _loadingConversations = false;
      notifyListeners();
    }
  }

  Future<void> refreshCalls() async {
    final number = _selectedNumber;
    if (number == null || _api == null) return;

    _loadingCalls = true;
    notifyListeners();

    try {
      _calls = await _api!.calls(number: number);
      _lastError = null;
    } on ApiException catch (error) {
      _lastError = error.message;
    } finally {
      _loadingCalls = false;
      notifyListeners();
    }
  }

  Future<void> loadThread(String peer) async {
    final number = _selectedNumber;
    if (number == null || _api == null) return;

    final messages = await _api!.messages(number, peer: peer);
    // Twilio returns newest first; a chat reads oldest first.
    _threads[peer] = messages.reversed.toList();
    _unreadPeers.remove(peer);
    notifyListeners();
  }

  Future<void> sendMessage({required String peer, required String body}) async {
    final number = _selectedNumber;
    if (number == null || _api == null) return;

    final sent = await _api!.sendMessage(from: number, to: peer, body: body);
    _threads.putIfAbsent(peer, () => []).add(sent);
    _patchConversationPreview(peer, body, sent.sentAt ?? DateTime.now());
    notifyListeners();
  }

  // ---------- realtime ----------

  void _onInboundMessage(InboundEvent event) {
    if (event.id > _lastEventId) _lastEventId = event.id;

    // An event for a different number of the same account is not this view's.
    if (event.to != _selectedNumber) return;

    final message = SmsMessage(
      sid: event.sid,
      from: event.from,
      to: event.to,
      body: event.body,
      direction: MessageDirection.inbound,
      status: 'received',
      numMedia: event.numMedia,
      sentAt: event.receivedAt,
    );

    final thread = _threads[event.from];
    if (thread != null && !thread.any((m) => m.sid == message.sid)) {
      thread.add(message);
    } else if (thread == null) {
      _unreadPeers.add(event.from);
    }

    _patchConversationPreview(event.from, event.body ?? '', event.receivedAt);
    notifyListeners();
  }

  void _onStatusUpdate(({String sid, String status}) update) {
    var changed = false;

    for (final entry in _threads.entries) {
      for (var i = 0; i < entry.value.length; i++) {
        final message = entry.value[i];
        if (message.sid != update.sid) continue;

        entry.value[i] = SmsMessage(
          sid: message.sid,
          from: message.from,
          to: message.to,
          body: message.body,
          direction: message.direction,
          status: update.status,
          numMedia: message.numMedia,
          sentAt: message.sentAt,
          errorMessage: message.errorMessage,
        );
        changed = true;
      }
    }

    if (changed) notifyListeners();
  }

  /// Replays anything the realtime feed missed while the app was backgrounded.
  Future<void> catchUp() async {
    if (_api == null || _phase != AppPhase.ready) return;

    try {
      final events = await _api!.events(since: _lastEventId);
      for (final event in events) {
        if (event.isMessage) _onInboundMessage(event);
        if (event.id > _lastEventId) _lastEventId = event.id;
      }
    } catch (error) {
      debugPrint('Catch-up failed: $error');
    }

    unawaited(_realtime?.connect());
    unawaited(_voice?.refreshTokenIfNeeded());
    await refreshConversations();
  }

  void _patchConversationPreview(String peer, String body, DateTime at) {
    final index = _conversations.indexWhere((c) => c.peerNumber == peer);
    final updated = Conversation(
      ownedNumber: _selectedNumber ?? '',
      peerNumber: peer,
      lastBody: body,
      lastAt: at,
      messageCount:
          index >= 0 ? _conversations[index].messageCount + 1 : 1,
    );

    if (index >= 0) {
      _conversations.removeAt(index);
    }
    _conversations.insert(0, updated);
  }

  void clearError() {
    if (_lastError == null) return;
    _lastError = null;
    notifyListeners();
  }

  void _setPhase(AppPhase phase) {
    _phase = phase;
    notifyListeners();
  }

  @override
  void dispose() {
    _tokenRefreshTimer?.cancel();
    _realtime?.dispose();
    _voice?.dispose();
    _api?.close();
    super.dispose();
  }
}
