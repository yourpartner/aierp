import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:yanxia_mobile/feature/notifications/bloc/notification_settings_bloc.dart';

class NotificationCenterPage extends StatelessWidget {
  const NotificationCenterPage({super.key});

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(title: const Text('通知中心')),
      body: BlocBuilder<NotificationSettingsBloc, NotificationSettingsState>(
        builder: (context, state) {
          final settings = state.settings;
          if (settings == null) {
            return const Center(child: CircularProgressIndicator());
          }

          return ListView(
            children: [
              SwitchListTile(
                title: const Text('启用推送通知'),
                subtitle: const Text('实时提醒批次处理状态'),
                value: settings.pushEnabled,
                onChanged: (value) => context
                    .read<NotificationSettingsBloc>()
                    .add(NotificationSettingsEventTogglePush(enabled: value)),
              ),
              SwitchListTile(
                title: const Text('启用汇总通知'),
                subtitle: Text('每天 ${settings.digestHour}:00 发送处理汇总'),
                value: settings.digestEnabled,
                onChanged: (value) => context
                    .read<NotificationSettingsBloc>()
                    .add(
                      NotificationSettingsEventUpdateDigest(
                        enabled: value,
                        digestHour: settings.digestHour,
                      ),
                    ),
              ),
            ],
          );
        },
      ),
    );
  }
}

