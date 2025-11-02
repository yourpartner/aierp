import 'package:equatable/equatable.dart';

import 'package:yanxia_mobile/core/models/captured_page_ref.dart';
import 'package:yanxia_mobile/core/models/capture_batch_status.dart';

class CaptureBatch extends Equatable {
  const CaptureBatch({
    required this.batchId,
    required this.context,
    this.pages = const [],
    this.status = CaptureBatchStatus.drafting,
    this.createdAt,
  });

  final String batchId;
  final CaptureContext context;
  final List<CapturedPageRef> pages;
  final CaptureBatchStatus status;
  final DateTime? createdAt;

  CaptureBatch copyWith({
    List<CapturedPageRef>? pages,
    CaptureBatchStatus? status,
  }) {
    return CaptureBatch(
      batchId: batchId,
      context: context,
      pages: pages ?? this.pages,
      status: status ?? this.status,
      createdAt: createdAt,
    );
  }

  @override
  List<Object?> get props => [batchId, context, pages, status, createdAt];
}

enum CaptureContext {
  invoiceVoucher,
  unknown,
}

