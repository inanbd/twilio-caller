import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../services/realtime_service.dart';
import '../../state/app_state.dart';
import '../widgets/number_picker.dart';
import 'calls_screen.dart';
import 'dialer_screen.dart';
import 'messages_screen.dart';
import 'settings_screen.dart';

class HomeScreen extends StatefulWidget {
  const HomeScreen({super.key});

  @override
  State<HomeScreen> createState() => _HomeScreenState();
}

class _HomeScreenState extends State<HomeScreen> {
  int _index = 0;

  static const _titles = ['Messages', 'Keypad', 'Calls', 'Settings'];

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final wide = MediaQuery.of(context).size.width >= 720;

    final body = IndexedStack(
      index: _index,
      children: const [
        MessagesScreen(),
        DialerScreen(),
        CallsScreen(),
        SettingsScreen(),
      ],
    );

    final destinations = [
      (Icons.message_outlined, Icons.message, 'Messages'),
      (Icons.dialpad_outlined, Icons.dialpad, 'Keypad'),
      (Icons.call_outlined, Icons.call, 'Calls'),
      (Icons.settings_outlined, Icons.settings, 'Settings'),
    ];

    return Scaffold(
      appBar: AppBar(
        title: Text(_titles[_index]),
        actions: [
          const _RealtimeIndicator(),
          if (app.numbers.length > 1) const NumberPicker(),
          const SizedBox(width: 8),
        ],
        bottom: app.hasProvisionedNumber
            ? null
            : const PreferredSize(
                preferredSize: Size.fromHeight(44),
                child: _NotProvisionedBanner(),
              ),
      ),
      body: wide
          ? Row(
              children: [
                NavigationRail(
                  selectedIndex: _index,
                  onDestinationSelected: (i) => setState(() => _index = i),
                  labelType: NavigationRailLabelType.all,
                  destinations: [
                    for (final (icon, selected, label) in destinations)
                      NavigationRailDestination(
                        icon: Icon(icon),
                        selectedIcon: Icon(selected),
                        label: Text(label),
                      ),
                  ],
                ),
                const VerticalDivider(width: 1),
                Expanded(child: body),
              ],
            )
          : body,
      bottomNavigationBar: wide
          ? null
          : NavigationBar(
              selectedIndex: _index,
              onDestinationSelected: (i) => setState(() => _index = i),
              destinations: [
                for (final (icon, selected, label) in destinations)
                  NavigationDestination(
                    icon: Icon(icon),
                    selectedIcon: Icon(selected),
                    label: label,
                  ),
              ],
            ),
    );
  }
}

/// Shows whether the push channel is live. When it is not, inbound messages only
/// appear on refresh, and the user deserves to know that rather than assume
/// nobody has written.
class _RealtimeIndicator extends StatelessWidget {
  const _RealtimeIndicator();

  @override
  Widget build(BuildContext context) {
    final realtime = context.watch<AppState>().realtime;
    if (realtime == null) return const SizedBox.shrink();

    return AnimatedBuilder(
      animation: realtime,
      builder: (context, _) {
        final (icon, colour, tooltip) = switch (realtime.status) {
          RealtimeStatus.connected =>
            (Icons.cloud_done_outlined, Colors.green, 'Live updates on'),
          RealtimeStatus.connecting || RealtimeStatus.reconnecting =>
            (Icons.cloud_sync_outlined, Colors.orange, 'Reconnecting…'),
          RealtimeStatus.disconnected => (
              Icons.cloud_off_outlined,
              Theme.of(context).colorScheme.outline,
              'Offline — pull to refresh for new messages',
            ),
        };

        return Tooltip(
          message: tooltip,
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 8),
            child: Icon(icon, size: 20, color: colour),
          ),
        );
      },
    );
  }
}

class _NotProvisionedBanner extends StatelessWidget {
  const _NotProvisionedBanner();

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Material(
      color: theme.colorScheme.errorContainer,
      child: InkWell(
        onTap: () => Navigator.of(context).push(MaterialPageRoute(
          builder: (_) => const SettingsScreen(openNumbers: true),
        )),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
          child: Row(
            children: [
              Icon(Icons.warning_amber_rounded,
                  size: 18, color: theme.colorScheme.onErrorContainer),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  'No number routes here yet — incoming calls and texts will not arrive.',
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: theme.colorScheme.onErrorContainer),
                ),
              ),
              Icon(Icons.chevron_right,
                  size: 18, color: theme.colorScheme.onErrorContainer),
            ],
          ),
        ),
      ),
    );
  }
}
