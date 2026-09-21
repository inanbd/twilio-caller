import 'package:intl/intl.dart';

/// Display helpers. Deliberately conservative: numbers are shown as Twilio
/// returns them (E.164) unless a well-known pattern is recognised, because a
/// wrong "prettified" number is worse than a plain one.
class Format {
  const Format._();

  static String phone(String raw) {
    if (raw.isEmpty) return 'Unknown';
    if (raw.startsWith('client:')) return raw.substring(7);

    final digits = raw.replaceAll(RegExp(r'[^0-9]'), '');
    if (raw.startsWith('+1') && digits.length == 11) {
      return '(${digits.substring(1, 4)}) ${digits.substring(4, 7)}-${digits.substring(7)}';
    }
    return raw;
  }

  static String timestamp(DateTime? value) {
    if (value == null) return '';

    final now = DateTime.now();
    final today = DateTime(now.year, now.month, now.day);
    final that = DateTime(value.year, value.month, value.day);

    if (that == today) return DateFormat.jm().format(value);
    if (today.difference(that).inDays == 1) return 'Yesterday';
    if (today.difference(that).inDays < 7) return DateFormat.E().format(value);
    if (value.year == now.year) return DateFormat.MMMd().format(value);
    return DateFormat.yMMMd().format(value);
  }

  static String fullTimestamp(DateTime? value) =>
      value == null ? '' : DateFormat.yMMMd().add_jm().format(value);

  static String duration(Duration d) {
    final minutes = d.inMinutes.remainder(60).toString().padLeft(2, '0');
    final seconds = d.inSeconds.remainder(60).toString().padLeft(2, '0');
    return d.inHours > 0 ? '${d.inHours}:$minutes:$seconds' : '$minutes:$seconds';
  }

  static String callDuration(int? seconds) =>
      seconds == null || seconds == 0 ? '' : duration(Duration(seconds: seconds));

  /// Normalises what the user typed into the dialler toward E.164, which is the
  /// only format Twilio accepts for the destination of a call or message.
  static String? toE164(String input, {String defaultCountryCode = '+1'}) {
    final trimmed = input.trim();
    if (trimmed.isEmpty) return null;
    if (trimmed.startsWith('client:')) return trimmed;

    if (trimmed.startsWith('+')) {
      final digits = trimmed.substring(1).replaceAll(RegExp(r'[^0-9]'), '');
      return digits.length >= 8 ? '+$digits' : null;
    }

    final digits = trimmed.replaceAll(RegExp(r'[^0-9]'), '');
    if (digits.length == 10) return '$defaultCountryCode$digits';
    if (digits.length == 11 && digits.startsWith('1')) return '+$digits';
    return digits.length >= 8 ? '+$digits' : null;
  }
}
