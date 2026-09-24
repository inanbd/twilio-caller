import 'package:flutter/material.dart';
import 'package:provider/provider.dart';

import '../services/voice_service.dart';
import '../state/app_state.dart';
import 'screens/auth_screen.dart';
import 'screens/connect_screen.dart';
import 'screens/home_screen.dart';
import 'screens/call_screen.dart';

/// Chooses the top-level screen and, whenever a call is in flight, puts the call
/// UI above everything else so the user is never left without a hang-up button.
class AppRoot extends StatefulWidget {
  const AppRoot({super.key});

  @override
  State<AppRoot> createState() => _AppRootState();
}

class _AppRootState extends State<AppRoot> with WidgetsBindingObserver {
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    // Realtime drops while backgrounded, so reconnect and replay on the way back.
    if (state == AppLifecycleState.resumed) {
      context.read<AppState>().catchUp();
    }
  }

  @override
  Widget build(BuildContext context) {
    final app = context.watch<AppState>();

    final screen = switch (app.phase) {
      AppPhase.starting => const _Splash(),
      AppPhase.needsAuth => const AuthScreen(),
      AppPhase.needsTwilio => const ConnectScreen(),
      AppPhase.ready => const HomeScreen(),
    };

    final voice = app.voice;
    if (voice == null) return screen;

    return ChangeNotifierProvider<VoiceService>.value(
      value: voice,
      child: Consumer<VoiceService>(
        builder: (context, voice, child) {
          if (!voice.state.isBusy) return child!;
          return const CallScreen();
        },
        child: screen,
      ),
    );
  }
}

class _Splash extends StatelessWidget {
  const _Splash();

  @override
  Widget build(BuildContext context) => const Scaffold(
        body: Center(child: CircularProgressIndicator()),
      );
}
