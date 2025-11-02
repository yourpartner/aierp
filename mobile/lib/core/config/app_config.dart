import 'package:yanxia_mobile/core/services/ai_event_stream.dart';
import 'package:yanxia_mobile/core/services/ai_service.dart';
import 'package:yanxia_mobile/core/services/azure_storage_service.dart';
import 'package:yanxia_mobile/core/services/notification_service.dart';

import 'package:yanxia_mobile/core/config/app_environment.dart';

class AppConfig {
  const AppConfig({
    required this.environment,
    required this.apiBaseUrl,
    required this.services,
  });

  final AppEnvironment environment;
  final Uri apiBaseUrl;
  final AppServices services;
}

class AppServices {
  const AppServices({
    required this.azureStorageService,
    required this.aiService,
    required this.notificationService,
    required this.aiEventStream,
  });

  final AzureStorageService azureStorageService;
  final AiService aiService;
  final NotificationService notificationService;
  final AiEventStream aiEventStream;
}

