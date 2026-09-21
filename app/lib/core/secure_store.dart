import 'package:flutter_secure_storage/flutter_secure_storage.dart';

/// Stores the backend session token. The Twilio API key secret is deliberately
/// not kept here: it is sent to the backend once at connect time and never
/// written to the device.
class SecureStore {
  static const _tokenKey = 'session_token';

  final FlutterSecureStorage _storage;

  SecureStore({FlutterSecureStorage? storage})
      // Defaults are the right thing here: the Android backend encrypts with the
      // keystore, iOS/macOS use the keychain, and web falls back to WebCrypto.
      : _storage = storage ?? const FlutterSecureStorage();

  Future<String?> readToken() async {
    try {
      return await _storage.read(key: _tokenKey);
    } catch (_) {
      // A corrupt or inaccessible keystore should log the user out, not crash.
      return null;
    }
  }

  Future<void> writeToken(String token) =>
      _storage.write(key: _tokenKey, value: token);

  Future<void> clear() async {
    try {
      await _storage.delete(key: _tokenKey);
    } catch (_) {
      // Nothing to delete.
    }
  }
}
