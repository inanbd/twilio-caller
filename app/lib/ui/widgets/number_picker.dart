import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../../core/formatting.dart';
import '../../state/app_state.dart';

/// Switches which of the account's numbers the app is acting as.
class NumberPicker extends StatelessWidget {
  const NumberPicker({super.key});

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();
    if (app.numbers.isEmpty) return const SizedBox.shrink();

    return PopupMenuButton<String>(
      tooltip: 'Acting as',
      onSelected: app.selectNumber,
      itemBuilder: (context) => [
        for (final number in app.numbers)
          PopupMenuItem(
            value: number.phoneNumber,
            child: Row(
              children: [
                Icon(
                  number.wiredToThisBackend
                      ? Icons.check_circle
                      : Icons.radio_button_unchecked,
                  size: 18,
                  color: number.wiredToThisBackend
                      ? Colors.green
                      : Theme.of(context).colorScheme.outline,
                ),
                const SizedBox(width: 10),
                Text(Format.phone(number.phoneNumber)),
              ],
            ),
          ),
      ],
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              Format.phone(app.selectedNumber ?? ''),
              style: Theme.of(context).textTheme.labelLarge,
            ),
            const Icon(Icons.arrow_drop_down),
          ],
        ),
      ),
    );
  }
}
