import 'package:flutter_test/flutter_test.dart';

import 'package:twilio_caller/core/config.dart';
import 'package:twilio_caller/core/formatting.dart';

void main() {
  group('BackendConfig.validate', () {
    test('rejects an empty URL', () {
      expect(BackendConfig.validate(''), isNotNull);
      expect(BackendConfig.validate(null), isNotNull);
    });

    test('rejects a bare hostname', () {
      expect(BackendConfig.validate('my-backend.example.com'), isNotNull);
    });

    test('accepts an https URL', () {
      expect(BackendConfig.validate('https://caller.example.com'), isNull);
    });

    test('rejects plain http to a remote host', () {
      // The Twilio API key secret crosses this connection exactly once.
      expect(BackendConfig.validate('http://caller.example.com'), isNotNull);
    });

    test('allows plain http to localhost for development', () {
      expect(BackendConfig.validate('http://localhost:5001'), isNull);
      expect(BackendConfig.validate('http://10.0.2.2:5001'), isNull);
    });

    test('normalise strips trailing slashes', () {
      expect(BackendConfig.normalise('https://x.test///'), 'https://x.test');
      expect(BackendConfig.normalise('  https://x.test  '), 'https://x.test');
    });
  });

  group('Format.toE164', () {
    test('passes through an already-E.164 number', () {
      expect(Format.toE164('+15558675309'), '+15558675309');
    });

    test('strips punctuation', () {
      expect(Format.toE164('+1 (555) 867-5309'), '+15558675309');
    });

    test('assumes the default country code for a ten-digit number', () {
      expect(Format.toE164('5558675309'), '+15558675309');
    });

    test('handles a leading country digit without a plus', () {
      expect(Format.toE164('15558675309'), '+15558675309');
    });

    test('rejects something too short to be a number', () {
      expect(Format.toE164('123'), isNull);
      expect(Format.toE164(''), isNull);
    });

    test('leaves a client identity alone', () {
      expect(Format.toE164('client:android_abc'), 'client:android_abc');
    });
  });

  group('Format.phone', () {
    test('formats a North American number', () {
      expect(Format.phone('+15558675309'), '(555) 867-5309');
    });

    test('leaves an international number as-is', () {
      expect(Format.phone('+442071234567'), '+442071234567');
    });

    test('unwraps a client identity', () {
      expect(Format.phone('client:android_abc'), 'android_abc');
    });

    test('handles an empty value', () {
      expect(Format.phone(''), 'Unknown');
    });
  });

  group('Format.duration', () {
    test('shows minutes and seconds under an hour', () {
      expect(Format.duration(const Duration(minutes: 3, seconds: 7)), '03:07');
    });

    test('adds hours when the call is long', () {
      expect(
        Format.duration(const Duration(hours: 1, minutes: 2, seconds: 3)),
        '1:02:03',
      );
    });
  });
}
