part of 'notification_settings_bloc.dart';

abstract class NotificationSettingsEvent extends Equatable {
  const NotificationSettingsEvent();

  @override
  List<Object?> get props => [];
}

class NotificationSettingsEventLoad extends NotificationSettingsEvent {
  const NotificationSettingsEventLoad();
}

class NotificationSettingsEventTogglePush extends NotificationSettingsEvent {
  const NotificationSettingsEventTogglePush({required this.enabled});

  final bool enabled;

  @override
  List<Object?> get props => [enabled];
}

class NotificationSettingsEventUpdateDigest extends NotificationSettingsEvent {
  const NotificationSettingsEventUpdateDigest({
    required this.enabled,
    this.digestHour,
  });

  final bool enabled;
  final int? digestHour;

  @override
  List<Object?> get props => [enabled, digestHour];
}

