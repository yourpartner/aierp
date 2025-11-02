part of 'notification_settings_bloc.dart';

class NotificationSettingsState extends Equatable {
  const NotificationSettingsState({
    required this.status,
    this.settings,
  });

  const NotificationSettingsState.initial()
      : status = NotificationSettingsStatus.loading,
        settings = null;

  final NotificationSettingsStatus status;
  final NotificationSettings? settings;

  NotificationSettingsState copyWith({
    NotificationSettingsStatus? status,
    NotificationSettings? settings,
  }) {
    return NotificationSettingsState(
      status: status ?? this.status,
      settings: settings ?? this.settings,
    );
  }

  @override
  List<Object?> get props => [status, settings];
}

enum NotificationSettingsStatus {
  loading,
  ready,
}

