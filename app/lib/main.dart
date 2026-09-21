import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import 'state/app_state.dart';
import 'ui/app_root.dart';

void main() {
  WidgetsFlutterBinding.ensureInitialized();

  runApp(
    ChangeNotifierProvider(
      create: (_) => AppState()..boot(),
      child: const TwilioCallerApp(),
    ),
  );
}

class TwilioCallerApp extends StatelessWidget {
  const TwilioCallerApp({super.key});

  @override
  Widget build(BuildContext context) {
    const seed = Color(0xFF0D6EFD);

    return MaterialApp(
      title: 'Twilio Caller',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        colorScheme: ColorScheme.fromSeed(seedColor: seed),
        useMaterial3: true,
      ),
      darkTheme: ThemeData(
        colorScheme: ColorScheme.fromSeed(
          seedColor: seed,
          brightness: Brightness.dark,
        ),
        useMaterial3: true,
      ),
      home: const AppRoot(),
    );
  }
}
