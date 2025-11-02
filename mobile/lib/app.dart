import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:yanxia_mobile/core/config/app_config.dart';
import 'package:yanxia_mobile/core/config/config_loader.dart';
import 'package:yanxia_mobile/core/logging/app_logger_observer.dart';
import 'package:yanxia_mobile/feature/capture/bloc/capture_batch_bloc.dart';
import 'package:yanxia_mobile/feature/chat/bloc/chat_timeline_bloc.dart';
import 'package:yanxia_mobile/feature/home/view/main_shell.dart';
import 'package:yanxia_mobile/feature/notifications/bloc/notification_settings_bloc.dart';

class YanxiaApp extends StatelessWidget {
  const YanxiaApp._({required this.config});

  final AppConfig config;

  static Future<Widget> bootstrap() async {
    final config = await ConfigLoader.load();

    Bloc.observer = AppLoggerObserver();

    return YanxiaApp._(config: config);
  }

  @override
  Widget build(BuildContext context) {
    return MultiBlocProvider(
      providers: [
        BlocProvider<CaptureBatchBloc>(
          create: (_) => CaptureBatchBloc(
            storageService: config.services.azureStorageService,
            aiService: config.services.aiService,
          ),
        ),
        BlocProvider<ChatTimelineBloc>(
          create: (_) => ChatTimelineBloc(
            aiEventStream: config.services.aiEventStream,
          ),
        ),
        BlocProvider<NotificationSettingsBloc>(
          create: (_) => NotificationSettingsBloc(
            notificationService: config.services.notificationService,
          )..add(const NotificationSettingsEventLoad()),
        ),
      ],
      child: MaterialApp(
        title: 'Yanxia Capture',
        theme: ThemeData(
          useMaterial3: true,
          colorScheme: ColorScheme.fromSeed(seedColor: const Color(0xFF1E40AF)),
        ),
        home: const MainShell(),
      ),
    );
  }
}

