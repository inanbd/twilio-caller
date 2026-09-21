import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../core/formatting.dart';
import '../../state/app_state.dart';
import 'thread_screen.dart';

/// The SMS inbox: one row per conversation partner for the active number.
class MessagesScreen extends StatelessWidget {
  const MessagesScreen({super.key});

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();

    if (app.selectedNumber == null) {
      return const _Empty(
        icon: Icons.sim_card_outlined,
        title: 'No number selected',
        detail: 'Buy or assign a number in the Twilio console, then pull to refresh.',
      );
    }

    return Scaffold(
      body: RefreshIndicator(
        onRefresh: app.refreshConversations,
        child: app.conversations.isEmpty
            ? ListView(
                children: [
                  SizedBox(height: MediaQuery.of(context).size.height * 0.2),
                  _Empty(
                    icon: Icons.forum_outlined,
                    title: app.loadingConversations
                        ? 'Loading conversations…'
                        : 'No messages yet',
                    detail:
                        'Texts sent to ${Format.phone(app.selectedNumber!)} will appear here.',
                  ),
                ],
              )
            : ListView.separated(
                itemCount: app.conversations.length,
                separatorBuilder: (_, _) => const Divider(height: 1, indent: 72),
                itemBuilder: (context, index) {
                  final conversation = app.conversations[index];
                  final unread = app.unreadPeers.contains(conversation.peerNumber);

                  return ListTile(
                    leading: CircleAvatar(
                      child: Text(_initials(conversation.peerNumber)),
                    ),
                    title: Text(
                      Format.phone(conversation.peerNumber),
                      style: TextStyle(
                        fontWeight: unread ? FontWeight.bold : FontWeight.w500,
                      ),
                    ),
                    subtitle: Text(
                      conversation.lastBody?.replaceAll('\n', ' ') ?? '',
                      maxLines: 1,
                      overflow: TextOverflow.ellipsis,
                      style: TextStyle(
                        fontWeight: unread ? FontWeight.w600 : FontWeight.normal,
                      ),
                    ),
                    trailing: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      crossAxisAlignment: CrossAxisAlignment.end,
                      children: [
                        Text(
                          Format.timestamp(conversation.lastAt),
                          style: Theme.of(context).textTheme.bodySmall,
                        ),
                        if (unread) ...[
                          const SizedBox(height: 4),
                          Container(
                            width: 8,
                            height: 8,
                            decoration: BoxDecoration(
                              color: Theme.of(context).colorScheme.primary,
                              shape: BoxShape.circle,
                            ),
                          ),
                        ],
                      ],
                    ),
                    onTap: () => Navigator.of(context).push(MaterialPageRoute(
                      builder: (_) => ThreadScreen(peer: conversation.peerNumber),
                    )),
                  );
                },
              ),
      ),
      floatingActionButton: FloatingActionButton(
        onPressed: () => _startNewMessage(context),
        child: const Icon(Icons.edit_outlined),
      ),
    );
  }

  static String _initials(String number) {
    final digits = number.replaceAll(RegExp(r'[^0-9]'), '');
    return digits.length >= 2
        ? digits.substring(digits.length - 2)
        : (digits.isEmpty ? '?' : digits);
  }

  Future<void> _startNewMessage(BuildContext context) async {
    final controller = TextEditingController();

    final peer = await showDialog<String>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('New message'),
        content: TextField(
          controller: controller,
          autofocus: true,
          keyboardType: TextInputType.phone,
          decoration: const InputDecoration(
            labelText: 'To',
            hintText: '+1 555 867 5309',
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () =>
                Navigator.of(context).pop(Format.toE164(controller.text)),
            child: const Text('Start'),
          ),
        ],
      ),
    );

    if (peer == null || !context.mounted) return;

    if (peer.isEmpty) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('That does not look like a phone number.')),
      );
      return;
    }

    await Navigator.of(context).push(MaterialPageRoute(
      builder: (_) => ThreadScreen(peer: peer),
    ));
  }
}

class _Empty extends StatelessWidget {
  final IconData icon;
  final String title;
  final String detail;

  const _Empty({required this.icon, required this.title, required this.detail});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Center(
      child: Padding(
        padding: const EdgeInsets.all(32),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 56, color: theme.colorScheme.outline),
            const SizedBox(height: 16),
            Text(title, style: theme.textTheme.titleMedium),
            const SizedBox(height: 8),
            Text(
              detail,
              textAlign: TextAlign.center,
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: theme.colorScheme.outline),
            ),
          ],
        ),
      ),
    );
  }
}
