import 'package:yanxia_mobile/core/config/app_config.dart';
import 'package:yanxia_mobile/core/config/app_environment.dart';
import 'package:yanxia_mobile/core/services/ai_event_stream.dart';
import 'package:yanxia_mobile/core/services/ai_service.dart';
import 'package:yanxia_mobile/core/services/azure_storage_service.dart';
import 'package:yanxia_mobile/core/services/notification_service.dart';

class ConfigLoader {
  const ConfigLoader._();

  static Future<AppConfig> load() async {
    const envValue = String.fromEnvironment('APP_ENV', defaultValue: 'dev');
    final environment = AppEnvironment.parse(envValue);

    final apiBaseUrl = Uri.parse(
      const String.fromEnvironment(
        'API_BASE_URL',
        defaultValue: 'https://api.example.com',
      ),
    );

    final services = AppServices(
      azureStorageService: StubAzureStorageService(),
      aiService: const StubAiService(),
      notificationService: StubNotificationService(),
      aiEventStream: StubAiEventStream(),
    );

    return AppConfig(
      environment: environment,
      apiBaseUrl: apiBaseUrl,
      services: services,
    );
  }
}

