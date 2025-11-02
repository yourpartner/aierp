part of 'capture_batch_bloc.dart';

class CaptureBatchState extends Equatable {
  const CaptureBatchState({
    required this.status,
    required this.pages,
    this.currentBatch,
    this.errorMessage,
  });

  const CaptureBatchState.initial()
      : status = CaptureBatchUiStatus.idle,
        pages = const [],
        currentBatch = null,
        errorMessage = null;

  final CaptureBatchUiStatus status;
  final List<CapturedPageRef> pages;
  final CaptureBatch? currentBatch;
  final String? errorMessage;

  CaptureBatchState copyWith({
    CaptureBatchUiStatus? status,
    List<CapturedPageRef>? pages,
    CaptureBatch? currentBatch,
    String? errorMessage,
  }) {
    return CaptureBatchState(
      status: status ?? this.status,
      pages: pages ?? this.pages,
      currentBatch: currentBatch ?? this.currentBatch,
      errorMessage: errorMessage,
    );
  }

  @override
  List<Object?> get props => [status, pages, currentBatch, errorMessage];
}

enum CaptureBatchUiStatus {
  idle,
  capturing,
  uploading,
  submitting,
  waiting,
  error,
}

