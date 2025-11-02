import 'package:equatable/equatable.dart';

import 'package:yanxia_mobile/core/models/capture_batch.dart';
import 'package:yanxia_mobile/core/models/capture_batch_status.dart';

abstract class AiService {
  Future<void> submitBatch(CaptureBatchSubmission submission);

  Future<CaptureBatch> getBatch(String batchId);
}

class CaptureBatchSubmission extends Equatable {
  const CaptureBatchSubmission({
    required this.batchId,
    required this.context,
    required this.pageIds,
  });

  final String batchId;
  final CaptureContext context;
  final List<String> pageIds;

  @override
  List<Object?> get props => [batchId, context, pageIds];
}

class StubAiService implements AiService {
  const StubAiService();

  @override
  Future<void> submitBatch(CaptureBatchSubmission submission) async {
    // TODO: 调用后端 AI 接口提交批次。
  }

  @override
  Future<CaptureBatch> getBatch(String batchId) async {
    // TODO: 请求后端获取批次处理状态。
    return CaptureBatch(
      batchId: batchId,
      context: CaptureContext.invoiceVoucher,
      status: CaptureBatchStatus.processing,
    );
  }
}

