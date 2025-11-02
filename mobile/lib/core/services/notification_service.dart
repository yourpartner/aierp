import 'package:equatable/equatable.dart';

abstract class NotificationService {
  Future<NotificationSettings> fetchSettings();

  Future<void> updateSettings(NotificationSettings settings);

  Future<void> registerPushToken(String token);
}

class NotificationSettings extends Equatable {
  const NotificationSettings({
    required this.pushEnabled,
    required this.digestEnabled,
    required this.digestHour,
  });

  final bool pushEnabled;
  final bool digestEnabled;
  final int digestHour;

  NotificationSettings copyWith({
    bool? pushEnabled,
    bool? digestEnabled,
    int? digestHour,
  }) {
    return NotificationSettings(
      pushEnabled: pushEnabled ?? this.pushEnabled,
      digestEnabled: digestEnabled ?? this.digestEnabled,
      digestHour: digestHour ?? this.digestHour,
    );
  }

  @override
  List<Object?> get props => [pushEnabled, digestEnabled, digestHour];
}

class StubNotificationService implements NotificationService {
  NotificationSettings _cache = const NotificationSettings(
    pushEnabled: true,
    digestEnabled: true,
    digestHour: 20,
  );

  @override
  Future<NotificationSettings> fetchSettings() async {
    return _cache;
  }

  @override
  Future<void> updateSettings(NotificationSettings settings) async {
    _cache = settings;
  }

  @override
  Future<void> registerPushToken(String token) async {
    // TODO: 上传 token 给后端。
  }
}

