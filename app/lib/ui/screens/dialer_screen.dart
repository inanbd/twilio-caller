import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../core/formatting.dart';
import '../../core/platform_support.dart';
import '../../state/app_state.dart';

/// The keypad. On platforms with the Voice SDK this starts real in-app audio;
/// elsewhere it offers dial-out, where Twilio rings the user's own handset and
/// bridges the call.
class DialerScreen extends StatefulWidget {
  final String? prefill;

  const DialerScreen({super.key, this.prefill});

  @override
  State<DialerScreen> createState() => _DialerScreenState();
}

class _DialerScreenState extends State<DialerScreen> {
  final _input = TextEditingController();
  bool _placing = false;

  @override
  void initState() {
    super.initState();
    if (widget.prefill != null) _input.text = widget.prefill!;
  }

  @override
  void dispose() {
    _input.dispose();
    super.dispose();
  }

  void _press(String key) {
    _input.text += key;
    _input.selection =
        TextSelection.collapsed(offset: _input.text.length);
    setState(() {});
  }

  void _backspace() {
    if (_input.text.isEmpty) return;
    _input.text = _input.text.substring(0, _input.text.length - 1);
    _input.selection = TextSelection.collapsed(offset: _input.text.length);
    setState(() {});
  }

  Future<void> _call() async {
    final app = context.read<AppState>();
    final from = app.selectedNumber;
    final to = Format.toE164(_input.text);

    if (from == null) {
      _toast('Select one of your Twilio numbers first.');
      return;
    }
    if (to == null) {
      _toast('That does not look like a phone number.');
      return;
    }

    setState(() => _placing = true);

    try {
      if (PlatformSupport.hasVoipSdk) {
        await app.voice!.requestPermissions();
        await app.voice!.placeCall(from: from, to: to);
      } else {
        await _dialOut(app, from, to);
      }
    } catch (error) {
      if (mounted) _toast('$error');
    } finally {
      if (mounted) setState(() => _placing = false);
    }
  }

  /// Without the Voice SDK the call cannot use this device's microphone, so ask
  /// which handset Twilio should ring before bridging.
  Future<void> _dialOut(AppState app, String from, String to) async {
    final fallback = app.session?.fallbackForwardNumber;
    final controller = TextEditingController(text: fallback ?? '');

    final bridgeTo = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Ring me first'),
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(PlatformSupport.voipUnavailableReason),
            const SizedBox(height: 16),
            TextField(
              controller: controller,
              autofocus: true,
              keyboardType: TextInputType.phone,
              decoration: const InputDecoration(
                labelText: 'Your phone number',
                hintText: '+1 555 000 1111',
              ),
            ),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () =>
                Navigator.of(context).pop(Format.toE164(controller.text)),
            child: const Text('Call'),
          ),
        ],
      ),
    );

    if (bridgeTo == null || bridgeTo.isEmpty) return;

    await app.voice!.dialOut(from: from, to: to, bridgeTo: bridgeTo);
    if (mounted) {
      _toast('Twilio is ringing $bridgeTo, then connecting you to ${Format.phone(to)}.');
    }
    await app.refreshCalls();
  }

  void _toast(String message) => ScaffoldMessenger.of(context)
      .showSnackBar(SnackBar(content: Text(message)));

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final app = context.watch<AppState>();

    return Scaffold(
      appBar: widget.prefill == null ? null : AppBar(title: const Text('Call')),
      body: SafeArea(
        child: Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 400),
            child: Column(
              children: [
                const Spacer(),
                Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 24),
                  child: TextField(
                    controller: _input,
                    textAlign: TextAlign.center,
                    style: theme.textTheme.headlineMedium,
                    keyboardType: TextInputType.phone,
                    decoration: const InputDecoration(
                      border: InputBorder.none,
                      hintText: 'Enter a number',
                    ),
                    onChanged: (_) => setState(() {}),
                  ),
                ),
                if (app.selectedNumber != null)
                  Text(
                    'Calling from ${Format.phone(app.selectedNumber!)}',
                    style: theme.textTheme.bodySmall
                        ?.copyWith(color: theme.colorScheme.outline),
                  ),
                if (!PlatformSupport.hasVoipSdk)
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: Text(
                      'Dial-out mode — Twilio will ring your phone first',
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: theme.colorScheme.tertiary),
                    ),
                  ),
                const SizedBox(height: 24),
                _Keypad(onKey: _press),
                const SizedBox(height: 16),
                Row(
                  mainAxisAlignment: MainAxisAlignment.center,
                  children: [
                    const SizedBox(width: 72),
                    SizedBox(
                      width: 72,
                      height: 72,
                      child: FloatingActionButton(
                        heroTag: 'call',
                        backgroundColor: Colors.green,
                        foregroundColor: Colors.white,
                        onPressed: _placing || _input.text.isEmpty ? null : _call,
                        child: _placing
                            ? const CircularProgressIndicator(color: Colors.white)
                            : const Icon(Icons.call, size: 30),
                      ),
                    ),
                    SizedBox(
                      width: 72,
                      child: _input.text.isEmpty
                          ? null
                          : IconButton(
                              icon: const Icon(Icons.backspace_outlined),
                              onPressed: _backspace,
                            ),
                    ),
                  ],
                ),
                const Spacer(),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _Keypad extends StatelessWidget {
  final void Function(String) onKey;

  const _Keypad({required this.onKey});

  static const _keys = [
    ('1', ''),
    ('2', 'ABC'),
    ('3', 'DEF'),
    ('4', 'GHI'),
    ('5', 'JKL'),
    ('6', 'MNO'),
    ('7', 'PQRS'),
    ('8', 'TUV'),
    ('9', 'WXYZ'),
    ('*', ''),
    ('0', '+'),
    ('#', ''),
  ];

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 32),
      child: GridView.count(
        crossAxisCount: 3,
        shrinkWrap: true,
        physics: const NeverScrollableScrollPhysics(),
        childAspectRatio: 1.35,
        children: [
          for (final (digit, letters) in _keys)
            InkResponse(
              onTap: () => onKey(digit),
              // Long-pressing zero types a plus, as on a real dialler.
              onLongPress: digit == '0' ? () => onKey('+') : null,
              radius: 36,
              child: Column(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  Text(digit, style: theme.textTheme.headlineSmall),
                  if (letters.isNotEmpty)
                    Text(
                      letters,
                      style: theme.textTheme.labelSmall
                          ?.copyWith(color: theme.colorScheme.outline),
                    ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}
