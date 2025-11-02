import 'package:flutter/material.dart';

import 'package:yanxia_mobile/app.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();

  final app = await YanxiaApp.bootstrap();

  runApp(app);
}

