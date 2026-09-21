import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../core/api_client.dart';
import '../../core/config.dart';
import '../../state/app_state.dart';

/// Two steps in one screen: point the app at its backend, then hand over the
/// Twilio API key once. After this the credentials live on the server only.
class ConnectScreen extends StatefulWidget {
  const ConnectScreen({super.key});

  @override
  State<ConnectScreen> createState() => _ConnectScreenState();
}

class _ConnectScreenState extends State<ConnectScreen> {
  final _formKey = GlobalKey<FormState>();
  final _backend = TextEditingController();
  final _accountSid = TextEditingController();
  final _apiKeySid = TextEditingController();
  final _apiKeySecret = TextEditingController();
  final _authToken = TextEditingController();

  bool _busy = false;
  bool _obscureSecret = true;
  bool _showAdvanced = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _backend.text = context.read<AppState>().backendUrl ?? '';
  }

  @override
  void dispose() {
    _backend.dispose();
    _accountSid.dispose();
    _apiKeySid.dispose();
    _apiKeySecret.dispose();
    _authToken.dispose();
    super.dispose();
  }

  Future<void> _submit() async {
    if (!_formKey.currentState!.validate()) return;

    setState(() {
      _busy = true;
      _error = null;
    });

    final app = context.read<AppState>();

    try {
      await app.setBackendUrl(_backend.text);
      await app.connect(
        accountSid: _accountSid.text,
        apiKeySid: _apiKeySid.text,
        apiKeySecret: _apiKeySecret.text,
        authToken: _authToken.text,
      );
    } on ApiException catch (error) {
      setState(() => _error = error.message);
    } catch (error) {
      setState(() => _error = 'Could not reach the backend. $error');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      body: SafeArea(
        child: Center(
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 460),
              child: Form(
                key: _formKey,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    Icon(Icons.phone_in_talk,
                        size: 56, color: theme.colorScheme.primary),
                    const SizedBox(height: 16),
                    Text('Twilio Caller',
                        textAlign: TextAlign.center,
                        style: theme.textTheme.headlineSmall),
                    const SizedBox(height: 8),
                    Text(
                      'Connect your Twilio account to call and text from your own numbers.',
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodyMedium
                          ?.copyWith(color: theme.colorScheme.outline),
                    ),
                    const SizedBox(height: 28),
                    TextFormField(
                      controller: _backend,
                      decoration: const InputDecoration(
                        labelText: 'Backend URL',
                        hintText: 'https://your-backend.example.com',
                        prefixIcon: Icon(Icons.dns_outlined),
                        border: OutlineInputBorder(),
                      ),
                      keyboardType: TextInputType.url,
                      autocorrect: false,
                      validator: BackendConfig.validate,
                    ),
                    const SizedBox(height: 16),
                    TextFormField(
                      controller: _accountSid,
                      decoration: const InputDecoration(
                        labelText: 'Account SID',
                        hintText: 'AC...',
                        prefixIcon: Icon(Icons.badge_outlined),
                        border: OutlineInputBorder(),
                      ),
                      autocorrect: false,
                      validator: (String? value) => _required(value, 'AC', 'Account SID'),
                    ),
                    const SizedBox(height: 16),
                    TextFormField(
                      controller: _apiKeySid,
                      decoration: const InputDecoration(
                        labelText: 'API Key SID',
                        hintText: 'SK...',
                        prefixIcon: Icon(Icons.key_outlined),
                        border: OutlineInputBorder(),
                      ),
                      autocorrect: false,
                      validator: (String? value) => _required(value, 'SK', 'API Key SID'),
                    ),
                    const SizedBox(height: 16),
                    TextFormField(
                      controller: _apiKeySecret,
                      obscureText: _obscureSecret,
                      decoration: InputDecoration(
                        labelText: 'API Key Secret',
                        prefixIcon: const Icon(Icons.lock_outline),
                        border: const OutlineInputBorder(),
                        suffixIcon: IconButton(
                          icon: Icon(_obscureSecret
                              ? Icons.visibility_outlined
                              : Icons.visibility_off_outlined),
                          onPressed: () =>
                              setState(() => _obscureSecret = !_obscureSecret),
                        ),
                      ),
                      autocorrect: false,
                      validator: (String? value) => (value == null || value.trim().isEmpty)
                          ? 'Enter the API key secret. Twilio shows it only once.'
                          : null,
                    ),
                    const SizedBox(height: 8),
                    Align(
                      alignment: Alignment.centerLeft,
                      child: TextButton.icon(
                        onPressed: () =>
                            setState(() => _showAdvanced = !_showAdvanced),
                        icon: Icon(_showAdvanced
                            ? Icons.expand_less
                            : Icons.expand_more),
                        label: const Text('Optional: auth token'),
                      ),
                    ),
                    if (_showAdvanced) ...[
                      TextFormField(
                        controller: _authToken,
                        obscureText: true,
                        decoration: const InputDecoration(
                          labelText: 'Auth Token (optional)',
                          prefixIcon: Icon(Icons.verified_user_outlined),
                          border: OutlineInputBorder(),
                        ),
                        autocorrect: false,
                      ),
                      const SizedBox(height: 6),
                      Text(
                        'Only used to verify that incoming webhooks really came from '
                        'Twilio. Leave blank to skip signature checks.',
                        style: theme.textTheme.bodySmall
                            ?.copyWith(color: theme.colorScheme.outline),
                      ),
                    ],
                    const SizedBox(height: 20),
                    if (_error != null) ...[
                      _ErrorBanner(message: _error!),
                      const SizedBox(height: 16),
                    ],
                    FilledButton.icon(
                      onPressed: _busy ? null : _submit,
                      icon: _busy
                          ? const SizedBox(
                              width: 18,
                              height: 18,
                              child: CircularProgressIndicator(strokeWidth: 2),
                            )
                          : const Icon(Icons.login),
                      label: Text(_busy ? 'Connecting…' : 'Connect'),
                      style: FilledButton.styleFrom(
                        padding: const EdgeInsets.symmetric(vertical: 16),
                      ),
                    ),
                    const SizedBox(height: 20),
                    Text(
                      'Your API key secret is sent to your backend once and stored '
                      'there encrypted. It is never kept on this device.',
                      textAlign: TextAlign.center,
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: theme.colorScheme.outline),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }

  /// Twilio's SIDs are prefixed, and mixing up the Account SID with the API Key
  /// SID is the single most common setup mistake, so it is caught here.
  String? _required(String? value, String prefix, String label) {
    final trimmed = value?.trim() ?? '';
    if (trimmed.isEmpty) return 'Enter your $label.';
    if (!trimmed.startsWith(prefix)) {
      return 'A $label starts with "$prefix".';
    }
    if (trimmed.length < 34) return 'That $label looks too short.';
    return null;
  }
}

class _ErrorBanner extends StatelessWidget {
  final String message;

  const _ErrorBanner({required this.message});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: theme.colorScheme.errorContainer,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(Icons.error_outline, color: theme.colorScheme.onErrorContainer),
          const SizedBox(width: 10),
          Expanded(
            child: Text(
              message,
              style: TextStyle(color: theme.colorScheme.onErrorContainer),
            ),
          ),
        ],
      ),
    );
  }
}
