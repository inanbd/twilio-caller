import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../core/formatting.dart';
import '../../state/app_state.dart';
import 'dialer_screen.dart';

/// Call history for the active number, pulled from Twilio.
class CallsScreen extends StatelessWidget {
  const CallsScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    final theme = Theme.of(context);

    return RefreshIndicator(
      onRefresh: app.refreshCalls,
      child: app.calls.isEmpty
          ? ListView(
              children: [
                SizedBox(height: MediaQuery.of(context).size.height * 0.25),
                Center(
                  child: Column(
                    children: [
                      Icon(Icons.call_outlined,
                          size: 56, color: theme.colorScheme.outline),
                      const SizedBox(height: 16),
                      Text(
                        app.loadingCalls ? 'Loading calls…' : 'No calls yet',
                        style: theme.textTheme.titleMedium,
                      ),
                    ],
                  ),
                ),
              ],
            )
          : ListView.separated(
              itemCount: app.calls.length,
              separatorBuilder: (_, _) => const Divider(height: 1, indent: 72),
              itemBuilder: (context, index) {
                final call = app.calls[index];
                final peer = call.isInbound ? call.from : call.to;

                return ListTile(
                  leading: CircleAvatar(
                    backgroundColor: call.wasMissed
                        ? theme.colorScheme.errorContainer
                        : theme.colorScheme.secondaryContainer,
                    child: Icon(
                      call.wasMissed
                          ? Icons.call_missed
                          : (call.isInbound
                              ? Icons.call_received
                              : Icons.call_made),
                      size: 20,
                      color: call.wasMissed
                          ? theme.colorScheme.onErrorContainer
                          : theme.colorScheme.onSecondaryContainer,
                    ),
                  ),
                  title: Text(
                    Format.phone(peer),
                    style: TextStyle(
                      color: call.wasMissed ? theme.colorScheme.error : null,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
                  subtitle: Text(
                    [
                      Format.fullTimestamp(call.startedAt),
                      if (Format.callDuration(call.durationSeconds).isNotEmpty)
                        Format.callDuration(call.durationSeconds),
                      if (call.wasMissed) call.status,
                    ].join(' · '),
                  ),
                  trailing: IconButton(
                    icon: const Icon(Icons.call),
                    onPressed: () => Navigator.of(context).push(
                      MaterialPageRoute(
                        builder: (_) => DialerScreen(prefill: peer),
                      ),
                    ),
                  ),
                );
              },
            ),
    );
  }
}
