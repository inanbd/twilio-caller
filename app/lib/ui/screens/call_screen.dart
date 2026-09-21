import 'dart:async';

import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../core/formatting.dart';
import '../../core/platform_support.dart';
import '../../services/voice_service.dart';

/// The in-call UI. Shown above everything else whenever a call is in flight, so
/// there is always a way to hang up.
class CallScreen extends StatefulWidget {
  const CallScreen({super.key});

  @override
  State<CallScreen> createState() => _CallScreenState();
}

class _CallScreenState extends State<CallScreen> {
  Timer? _ticker;

  @override
  void initState() {
    super.initState();
    // Drives the call duration readout.
    _ticker = Timer.periodic(const Duration(seconds: 1), (_) {
      if (mounted) setState(() {});
    });
  }

  @override
  void dispose() {
    _ticker?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final voice = context.watch<VoiceService>();
    final state = voice.state;
    final theme = Theme.of(context);

    final elapsed = state.connectedAt == null
        ? null
        : DateTime.now().difference(state.connectedAt!);

    return Scaffold(
      backgroundColor: theme.colorScheme.surfaceContainerHighest,
      body: SafeArea(
        child: Column(
          children: [
            const Spacer(flex: 2),
            CircleAvatar(
              radius: 56,
              backgroundColor: theme.colorScheme.primaryContainer,
              child: Icon(
                state.isInbound ? Icons.call_received : Icons.call_made,
                size: 44,
                color: theme.colorScheme.onPrimaryContainer,
              ),
            ),
            const SizedBox(height: 24),
            Text(
              Format.phone(state.peerNumber),
              style: theme.textTheme.headlineSmall,
            ),
            const SizedBox(height: 8),
            Text(
              switch (state.phase) {
                CallPhase.connecting => 'Connecting…',
                CallPhase.ringing =>
                  state.isInbound ? 'Incoming call' : 'Ringing…',
                CallPhase.active => Format.duration(elapsed ?? Duration.zero),
                CallPhase.ended => 'Call ended',
                CallPhase.idle => '',
              },
              style: theme.textTheme.titleMedium
                  ?.copyWith(color: theme.colorScheme.outline),
            ),
            if (state.ownedNumber.isNotEmpty) ...[
              const SizedBox(height: 4),
              Text(
                state.isInbound
                    ? 'to ${Format.phone(state.ownedNumber)}'
                    : 'from ${Format.phone(state.ownedNumber)}',
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: theme.colorScheme.outline),
              ),
            ],
            if (state.error != null) ...[
              const SizedBox(height: 16),
              Padding(
                padding: const EdgeInsets.symmetric(horizontal: 32),
                child: Text(
                  state.error!,
                  textAlign: TextAlign.center,
                  style: TextStyle(color: theme.colorScheme.error),
                ),
              ),
            ],
            const Spacer(flex: 2),
            if (state.phase == CallPhase.active)
              Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: [
                  _RoundAction(
                    icon: state.isMuted ? Icons.mic_off : Icons.mic,
                    label: state.isMuted ? 'Unmute' : 'Mute',
                    active: state.isMuted,
                    onPressed: voice.toggleMute,
                  ),
                  if (PlatformSupport.hasSystemCallUi)
                    _RoundAction(
                      icon: state.isSpeakerOn
                          ? Icons.volume_up
                          : Icons.volume_down_outlined,
                      label: 'Speaker',
                      active: state.isSpeakerOn,
                      onPressed: voice.toggleSpeaker,
                    ),
                  _RoundAction(
                    icon: Icons.dialpad,
                    label: 'Keypad',
                    active: false,
                    onPressed: () => _showDtmfPad(context, voice),
                  ),
                ],
              ),
            const SizedBox(height: 32),
            _CallActions(voice: voice),
            const SizedBox(height: 40),
          ],
        ),
      ),
    );
  }

  Future<void> _showDtmfPad(BuildContext context, VoiceService voice) async {
    await showModalBottomSheet<void>(
      context: context,
      builder: (context) => SafeArea(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: GridView.count(
            crossAxisCount: 3,
            shrinkWrap: true,
            childAspectRatio: 1.6,
            children: [
              for (final key in [
                '1', '2', '3', '4', '5', '6', '7', '8', '9', '*', '0', '#'
              ])
                InkResponse(
                  onTap: () => voice.sendDigits(key),
                  child: Center(
                    child: Text(
                      key,
                      style: Theme.of(context).textTheme.headlineSmall,
                    ),
                  ),
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _CallActions extends StatelessWidget {
  final VoiceService voice;

  const _CallActions({required this.voice});

  @override
  Widget build(BuildContext context) {
    final state = voice.state;

    if (state.phase == CallPhase.ended) {
      return FilledButton.icon(
        onPressed: voice.reset,
        icon: const Icon(Icons.check),
        label: const Text('Done'),
      );
    }

    // An unanswered inbound call gets both buttons; everything else just hangs up.
    if (state.isInbound && state.phase == CallPhase.ringing) {
      return Row(
        mainAxisAlignment: MainAxisAlignment.spaceEvenly,
        children: [
          _BigButton(
            icon: Icons.call_end,
            colour: Colors.red,
            label: 'Decline',
            onPressed: voice.hangUp,
          ),
          _BigButton(
            icon: Icons.call,
            colour: Colors.green,
            label: 'Answer',
            onPressed: voice.answer,
          ),
        ],
      );
    }

    return _BigButton(
      icon: Icons.call_end,
      colour: Colors.red,
      label: 'End',
      onPressed: voice.hangUp,
    );
  }
}

class _BigButton extends StatelessWidget {
  final IconData icon;
  final Color colour;
  final String label;
  final VoidCallback onPressed;

  const _BigButton({
    required this.icon,
    required this.colour,
    required this.label,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) => Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          SizedBox(
            width: 72,
            height: 72,
            child: FloatingActionButton(
              heroTag: label,
              backgroundColor: colour,
              foregroundColor: Colors.white,
              onPressed: onPressed,
              child: Icon(icon, size: 30),
            ),
          ),
          const SizedBox(height: 8),
          Text(label, style: Theme.of(context).textTheme.bodySmall),
        ],
      );
}

class _RoundAction extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool active;
  final VoidCallback onPressed;

  const _RoundAction({
    required this.icon,
    required this.label,
    required this.active,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 16),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          IconButton.filledTonal(
            onPressed: onPressed,
            isSelected: active,
            icon: Icon(icon),
            iconSize: 26,
            padding: const EdgeInsets.all(14),
            style: active
                ? IconButton.styleFrom(
                    backgroundColor: theme.colorScheme.primary,
                    foregroundColor: theme.colorScheme.onPrimary,
                  )
                : null,
          ),
          const SizedBox(height: 6),
          Text(label, style: theme.textTheme.bodySmall),
        ],
      ),
    );
  }
}
