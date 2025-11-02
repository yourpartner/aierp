import 'dart:async';
import 'dart:typed_data';

import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';
import 'package:uuid/uuid.dart';

import 'package:yanxia_mobile/core/models/capture_batch.dart';
import 'package:yanxia_mobile/core/models/capture_batch_status.dart';
import 'package:yanxia_mobile/core/models/captured_page_ref.dart';
import 'package:yanxia_mobile/core/services/ai_service.dart';
import 'package:yanxia_mobile/core/services/azure_storage_service.dart';

part 'capture_batch_event.dart';
part 'capture_batch_state.dart';

class CaptureBatchBloc extends Bloc<CaptureBatchEvent, CaptureBatchState> {
  CaptureBatchBloc({
    required this.storageService,
    required this.aiService,
  }) : super(const CaptureBatchState.initial()) {
    on<CaptureBatchStarted>(_onStarted);
    on<CaptureBatchNewSessionRequested>(_onNewSessionRequested);
    on<CaptureBatchPageCaptured>(_onPageCaptured, transformer: sequential());
    on<CaptureBatchPageRemoved>(_onPageRemoved);
    on<CaptureBatchSubmitted>(_onSubmitted, transformer: sequential());
    on<CaptureBatchRefreshRequested>(_onRefreshRequested);
  }

  final AzureStorageService storageService;
  final AiService aiService;
  final Uuid _uuid = const Uuid();

  FutureOr<void> _onStarted(
    CaptureBatchStarted event,
    Emitter<CaptureBatchState> emit,
  ) {
    emit(state.copyWith(status: CaptureBatchUiStatus.idle));
  }

  FutureOr<void> _onNewSessionRequested(
    CaptureBatchNewSessionRequested event,
    Emitter<CaptureBatchState> emit,
  ) {
    final batchId = _uuid.v4();
    emit(
      state.copyWith(
        currentBatch: CaptureBatch(
          batchId: batchId,
          context: event.context,
          status: CaptureBatchStatus.drafting,
          createdAt: DateTime.now(),
        ),
        pages: const [],
        status: CaptureBatchUiStatus.capturing,
        errorMessage: null,
      ),
    );
  }

  Future<void> _onPageCaptured(
    CaptureBatchPageCaptured event,
    Emitter<CaptureBatchState> emit,
  ) async {
    final batch = state.currentBatch;
    if (batch == null) {
      emit(state.copyWith(errorMessage: '当前没有激活的批次'));
      return;
    }

    emit(state.copyWith(status: CaptureBatchUiStatus.uploading));

    try {
      final uploadRequest = CapturePageUploadRequest(
        batchId: batch.batchId,
        pageNumber: state.pages.length + 1,
        bytes: event.bytes,
        contentType: event.contentType,
      );
      final pageRef = await storageService.uploadPage(uploadRequest);

      final pages = List<CapturedPageRef>.from(state.pages)..add(pageRef);
      emit(
        state.copyWith(
          pages: pages,
          status: CaptureBatchUiStatus.capturing,
        ),
      );
    } catch (error) {
      emit(
        state.copyWith(
          status: CaptureBatchUiStatus.error,
          errorMessage: '上传失败，请重试',
        ),
      );
    }
  }

  Future<void> _onPageRemoved(
    CaptureBatchPageRemoved event,
    Emitter<CaptureBatchState> emit,
  ) async {
    final pages = List<CapturedPageRef>.from(state.pages);
    final index = pages.indexWhere((element) => element.pageId == event.pageId);
    if (index == -1) {
      return;
    }

    pages.removeAt(index);
    emit(state.copyWith(pages: pages));

    unawaited(storageService.deletePage(event.pageId));
  }

  Future<void> _onSubmitted(
    CaptureBatchSubmitted event,
    Emitter<CaptureBatchState> emit,
  ) async {
    final batch = state.currentBatch;
    if (batch == null || state.pages.isEmpty) {
      emit(
        state.copyWith(
          status: CaptureBatchUiStatus.error,
          errorMessage: '请先完成扫描',
        ),
      );
      return;
    }

    emit(state.copyWith(status: CaptureBatchUiStatus.submitting));

    try {
      await aiService.submitBatch(
        CaptureBatchSubmission(
          batchId: batch.batchId,
          context: batch.context,
          pageIds: state.pages.map((e) => e.pageId).toList(),
        ),
      );

      emit(
        state.copyWith(
          status: CaptureBatchUiStatus.waiting,
          currentBatch: batch.copyWith(status: CaptureBatchStatus.submitted),
        ),
      );
    } catch (error) {
      emit(
        state.copyWith(
          status: CaptureBatchUiStatus.error,
          errorMessage: '提交失败，请稍后重试',
        ),
      );
    }
  }

  Future<void> _onRefreshRequested(
    CaptureBatchRefreshRequested event,
    Emitter<CaptureBatchState> emit,
  ) async {
    final batch = state.currentBatch;
    if (batch == null) {
      return;
    }

    final latest = await aiService.getBatch(batch.batchId);
    emit(
      state.copyWith(
        currentBatch: latest,
        status: state.status,
      ),
    );
  }
}

EventTransformer<E> sequential<E>() {
  return (events, mapper) => events.asyncExpand(mapper);
}

