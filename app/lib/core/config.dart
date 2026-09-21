import 'package:shared_preferences/shared_preferences.dart';

/// Where the companion backend lives.
///
/// There is no sensible default: the backend is self-hosted, so the URL is baked
/// in at build time with `--dart-define=BACKEND_URL=...` or typed once on the
/// connect screen and remembered from then on.
class BackendConfig {
  static const String _prefsKey = 'backend_url';
  static const String _compiledIn = String.fromEnvironment('BACKEND_URL');

  const BackendConfig._();

  static Future<String?> load() async {
    final prefs = await SharedPreferences.getInstance();
    final stored = prefs.getString(_prefsKey);
    if (stored != null && stored.isNotEmpty) return stored;
    return _compiledIn.isEmpty ? null : _compiledIn;
  }

  static Future<void> save(String url) async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.setString(_prefsKey, normalise(url));
  }

  static Future<void> clear() async {
    final prefs = await SharedPreferences.getInstance();
    await prefs.remove(_prefsKey);
  }

  /// Trims the trailing slash so path joining never produces a double slash.
  static String normalise(String url) => url.trim().replaceAll(RegExp(r'/+$'), '');

  /// The backend signs Twilio tokens, so an http URL would put the Twilio
  /// credentials on the wire in the clear. Localhost is exempt for development.
  static String? validate(String? url) {
    final trimmed = url?.trim() ?? '';
    if (trimmed.isEmpty) return 'Enter the URL of your backend.';

    final uri = Uri.tryParse(trimmed);
    if (uri == null || !uri.isAbsolute) {
      return 'That is not a complete URL. Include https://';
    }
    if (uri.scheme != 'https' && uri.scheme != 'http') {
      return 'The URL must start with https://';
    }
    final isLocal = uri.host == 'localhost' ||
        uri.host == '127.0.0.1' ||
        uri.host == '10.0.2.2';
    if (uri.scheme == 'http' && !isLocal) {
      return 'Use https:// so your Twilio credentials are encrypted in transit.';
    }
    return null;
  }
}
