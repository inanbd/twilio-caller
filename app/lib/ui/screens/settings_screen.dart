import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../core/api_client.dart';
import '../../core/formatting.dart';
import '../../core/platform_support.dart';
import '../../state/app_state.dart';

/// Account details, number routing, and sign-out.
class SettingsScreen extends StatefulWidget {
  /// Set when the user arrives here from the "not provisioned" banner, so the
  /// routing sheet opens straight away.
  final bool openNumbers;

  const SettingsScreen({super.key, this.openNumbers = false});

  @override
  State<SettingsScreen> createState() => _SettingsScreenState();
}

class _SettingsScreenState extends State<SettingsScreen> {
  @override
  void initState() {
    super.initState();
    // Opened once on arrival. Doing this from build() would re-open the sheet
    // on every rebuild, and this screen rebuilds whenever AppState changes.
    if (widget.openNumbers) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) _showNumbers(context);
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final session = app.session;

    return Scaffold(
      appBar: widget.openNumbers ? AppBar(title: const Text('Settings')) : null,
      body: ListView(
        children: [
          if (session != null) ...[
            const _SectionHeader('Twilio account'),
            ListTile(
              leading: const Icon(Icons.account_circle_outlined),
              title: Text(session.friendlyName),
              subtitle: Text(session.accountSid),
            ),
            ListTile(
              leading: const Icon(Icons.dns_outlined),
              title: const Text('Backend'),
              subtitle: Text(app.backendUrl ?? 'unknown'),
            ),
          ],
          const Divider(),
          const _SectionHeader('Numbers'),
          ListTile(
            leading: const Icon(Icons.sim_card_outlined),
            title: const Text('Phone numbers & routing'),
            subtitle: Text(
              '${app.numbers.length} number${app.numbers.length == 1 ? '' : 's'}'
              ' · ${app.numbers.where((n) => n.wiredToThisBackend).length} routed here',
            ),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => _showNumbers(context),
          ),
          ListTile(
            leading: const Icon(Icons.phone_forwarded_outlined),
            title: const Text('Fallback forwarding'),
            subtitle: Text(
              session?.fallbackForwardNumber == null
                  ? 'Not set — calls ring only in the app'
                  : 'Calls ring ${Format.phone(session!.fallbackForwardNumber!)}'
                      ' when the app is unreachable',
            ),
            trailing: const Icon(Icons.chevron_right),
            onTap: () => _showNumbers(context),
          ),
          const Divider(),
          const _SectionHeader('This device'),
          ListTile(
            leading: Icon(
              PlatformSupport.hasVoipSdk
                  ? Icons.headset_mic_outlined
                  : Icons.phonelink_ring_outlined,
            ),
            title: Text(
              PlatformSupport.hasVoipSdk
                  ? 'In-app calling'
                  : 'Dial-out calling only',
            ),
            subtitle: Text(
              PlatformSupport.hasVoipSdk
                  ? (app.voice?.isRegistered ?? false)
                      ? 'Registered as ${session?.identity ?? ''}'
                      : 'Not registered — provision a number to enable calling'
                  : PlatformSupport.voipUnavailableReason,
            ),
          ),
          const Divider(),
          ListTile(
            leading: Icon(Icons.logout, color: Theme.of(context).colorScheme.error),
            title: Text(
              'Sign out',
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
            subtitle: const Text(
              'Forgets this device\'s session. Your credentials stay on the backend.',
            ),
            onTap: () => _confirmSignOut(context),
          ),
          const SizedBox(height: 24),
        ],
      ),
    );
  }

  void _showNumbers(BuildContext context) => showModalBottomSheet<void>(
        context: context,
        isScrollControlled: true,
        builder: (_) => const _NumbersSheet(),
      );

  Future<void> _confirmSignOut(BuildContext context) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Sign out?'),
        content: const Text(
          'You will need your Twilio API key and secret to sign in again.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Sign out'),
          ),
        ],
      ),
    );

    if (confirmed == true && context.mounted) {
      await context.read<AppState>().signOut();
    }
  }
}

/// Picks which numbers route inbound calls and texts to this backend, and sets
/// the fallback handset. This is what makes receiving work at all.
class _NumbersSheet extends StatefulWidget {
  const _NumbersSheet();

  @override
  State<_NumbersSheet> createState() => _NumbersSheetState();
}

class _NumbersSheetState extends State<_NumbersSheet> {
  late Set<String> _selected;
  late TextEditingController _fallback;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    final app = context.read<AppState>();
    _selected = app.numbers
        .where((n) => n.wiredToThisBackend)
        .map((n) => n.sid)
        .toSet();
    _fallback =
        TextEditingController(text: app.session?.fallbackForwardNumber ?? '');
  }

  @override
  void dispose() {
    _fallback.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() => _saving = true);

    try {
      final result = await context.read<AppState>().provision(
            _selected.toList(),
            fallbackForwardNumber: Format.toE164(_fallback.text),
          );

      if (!mounted) return;
      Navigator.of(context).pop();

      final message = result.failures.isEmpty
          ? '${result.updatedNumberSids.length} number'
              '${result.updatedNumberSids.length == 1 ? '' : 's'} now route here.'
          : 'Some numbers could not be updated: ${result.failures.join('; ')}';

      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(message)));
    } on ApiException catch (error) {
      if (mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(error.message)));
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final theme = Theme.of(context);

    return DraggableScrollableSheet(
      expand: false,
      initialChildSize: 0.75,
      maxChildSize: 0.95,
      builder: (context, scrollController) => Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 16, 16, 8),
            child: Row(
              children: [
                Expanded(
                  child: Text('Number routing',
                      style: theme.textTheme.titleLarge),
                ),
                IconButton(
                  icon: const Icon(Icons.refresh),
                  onPressed: app.refreshNumbers,
                ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 16),
            child: Text(
              'Selected numbers have their voice and SMS webhooks pointed at your '
              'backend, so calls and texts reach this app. Unselected numbers are '
              'left exactly as they are in the Twilio console.',
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: theme.colorScheme.outline),
            ),
          ),
          const SizedBox(height: 8),
          Expanded(
            child: ListView(
              controller: scrollController,
              children: [
                for (final number in app.numbers)
                  CheckboxListTile(
                    value: _selected.contains(number.sid),
                    onChanged: (checked) => setState(() {
                      if (checked == true) {
                        _selected.add(number.sid);
                      } else {
                        _selected.remove(number.sid);
                      }
                    }),
                    title: Text(Format.phone(number.phoneNumber)),
                    subtitle: Text([
                      if (number.voiceEnabled) 'Voice',
                      if (number.smsEnabled) 'SMS',
                      if (number.mmsEnabled) 'MMS',
                      if (number.wiredToThisBackend) 'routed here',
                    ].join(' · ')),
                    secondary: number.wiredToThisBackend
                        ? const Icon(Icons.check_circle, color: Colors.green)
                        : const Icon(Icons.radio_button_unchecked),
                  ),
                const Divider(),
                Padding(
                  padding: const EdgeInsets.all(16),
                  child: TextField(
                    controller: _fallback,
                    keyboardType: TextInputType.phone,
                    decoration: const InputDecoration(
                      labelText: 'Fallback forwarding number (optional)',
                      helperText:
                          'Rings this number when no app is available to take a call.',
                      helperMaxLines: 2,
                      border: OutlineInputBorder(),
                    ),
                  ),
                ),
              ],
            ),
          ),
          SafeArea(
            top: false,
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: SizedBox(
                width: double.infinity,
                child: FilledButton.icon(
                  onPressed: _saving ? null : _save,
                  icon: _saving
                      ? const SizedBox(
                          width: 18,
                          height: 18,
                          child: CircularProgressIndicator(strokeWidth: 2),
                        )
                      : const Icon(Icons.save_outlined),
                  label: Text(_saving ? 'Updating Twilio…' : 'Save routing'),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}

class _SectionHeader extends StatelessWidget {
  final String title;

  const _SectionHeader(this.title);

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 4),
        child: Text(
          title.toUpperCase(),
          style: Theme.of(context).textTheme.labelSmall?.copyWith(
                color: Theme.of(context).colorScheme.primary,
                letterSpacing: 1.2,
              ),
        ),
      );
}
