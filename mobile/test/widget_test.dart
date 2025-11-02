// This is a basic Flutter widget test.
//
// To perform an interaction with a widget in your test, use the WidgetTester
// utility in the flutter_test package. For example, you can send tap and scroll
// gestures. You can also use WidgetTester to find child widgets in the widget
// tree, read text, and verify that the values of widget properties are correct.

import 'package:flutter_test/flutter_test.dart';

import 'package:yanxia_mobile/app.dart';

void main() {
  testWidgets('Yanxia app renders capture workspace', (tester) async {
    final app = await YanxiaApp.bootstrap();

    await tester.pumpWidget(app);
    await tester.pumpAndSettle();

    expect(find.text('多页扫描'), findsOneWidget);
  });
}
