import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:logger/logger.dart';

class AppLoggerObserver extends BlocObserver {
  AppLoggerObserver();

  final Logger _logger = Logger();

  @override
  void onEvent(Bloc bloc, Object? event) {
    _logger.i('[${bloc.runtimeType}] Event: $event');
    super.onEvent(bloc, event);
  }

  @override
  void onChange(BlocBase bloc, Change change) {
    _logger.d('[${bloc.runtimeType}] Change: $change');
    super.onChange(bloc, change);
  }

  @override
  void onError(BlocBase bloc, Object error, StackTrace stackTrace) {
    _logger.e(
      '[${bloc.runtimeType}] Error: $error',
      error: error,
      stackTrace: stackTrace,
    );
    super.onError(bloc, error, stackTrace);
  }
}

