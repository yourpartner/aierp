import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import 'package:yanxia_mobile/core/services/notification_service.dart';

part 'notification_settings_event.dart';
part 'notification_settings_state.dart';

class NotificationSettingsBloc
    extends Bloc<NotificationSettingsEvent, NotificationSettingsState> {
  NotificationSettingsBloc({required this.notificationService})
      : super(const NotificationSettingsState.initial()) {
    on<NotificationSettingsEventLoad>(_onLoad);
    on<NotificationSettingsEventTogglePush>(_onTogglePush);
    on<NotificationSettingsEventUpdateDigest>(_onUpdateDigest);
  }

  final NotificationService notificationService;

  Future<void> _onLoad(
    NotificationSettingsEventLoad event,
    Emitter<NotificationSettingsState> emit,
  ) async {
    emit(state.copyWith(status: NotificationSettingsStatus.loading));
    final settings = await notificationService.fetchSettings();
    emit(
      state.copyWith(
        status: NotificationSettingsStatus.ready,
        settings: settings,
      ),
    );
  }

  Future<void> _onTogglePush(
    NotificationSettingsEventTogglePush event,
    Emitter<NotificationSettingsState> emit,
  ) async {
    final settings = state.settings;
    if (settings == null) {
      return;
    }
    final updated = settings.copyWith(pushEnabled: event.enabled);
    emit(state.copyWith(settings: updated));
    await notificationService.updateSettings(updated);
  }

  Future<void> _onUpdateDigest(
    NotificationSettingsEventUpdateDigest event,
    Emitter<NotificationSettingsState> emit,
  ) async {
    final settings = state.settings;
    if (settings == null) {
      return;
    }
    final updated = settings.copyWith(
      digestEnabled: event.enabled,
      digestHour: event.digestHour ?? settings.digestHour,
    );
    emit(state.copyWith(settings: updated));
    await notificationService.updateSettings(updated);
  }
}

