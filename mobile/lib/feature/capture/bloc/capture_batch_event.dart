part of 'capture_batch_bloc.dart';

abstract class CaptureBatchEvent extends Equatable {
  const CaptureBatchEvent();

  @override
  List<Object?> get props => [];
}

class CaptureBatchStarted extends CaptureBatchEvent {
  const CaptureBatchStarted();
}

class CaptureBatchNewSessionRequested extends CaptureBatchEvent {
  const CaptureBatchNewSessionRequested({required this.context});

  final CaptureContext context;

  @override
  List<Object?> get props => [context];
}

class CaptureBatchPageCaptured extends CaptureBatchEvent {
  const CaptureBatchPageCaptured({
    required this.bytes,
    required this.contentType,
  });

  final Uint8List bytes;
  final String contentType;

  @override
  List<Object?> get props => [bytes, contentType];
}

class CaptureBatchPageRemoved extends CaptureBatchEvent {
  const CaptureBatchPageRemoved({required this.pageId});

  final String pageId;

  @override
  List<Object?> get props => [pageId];
}

class CaptureBatchSubmitted extends CaptureBatchEvent {
  const CaptureBatchSubmitted();
}

class CaptureBatchRefreshRequested extends CaptureBatchEvent {
  const CaptureBatchRefreshRequested();
}

