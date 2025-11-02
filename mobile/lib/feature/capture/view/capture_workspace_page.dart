import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import 'package:yanxia_mobile/core/models/capture_batch.dart';
import 'package:yanxia_mobile/core/models/captured_page_ref.dart';
import 'package:yanxia_mobile/feature/capture/bloc/capture_batch_bloc.dart';

class CaptureWorkspacePage extends StatefulWidget {
  const CaptureWorkspacePage({super.key});

  @override
  State<CaptureWorkspacePage> createState() => _CaptureWorkspacePageState();
}

class _CaptureWorkspacePageState extends State<CaptureWorkspacePage> {
  @override
  void initState() {
    super.initState();
    context.read<CaptureBatchBloc>().add(const CaptureBatchStarted());
  }

  @override
  Widget build(BuildContext context) {
    return Scaffold(
      appBar: AppBar(
        title: const Text('多页扫描'),
        actions: [
          IconButton(
            onPressed: () => _startNewBatch(context),
            icon: const Icon(Icons.add_to_photos_outlined),
          ),
        ],
      ),
      body: BlocBuilder<CaptureBatchBloc, CaptureBatchState>(
        builder: (context, state) {
          if (state.currentBatch == null) {
            return _EmptyPlaceholder(onNewBatch: () => _startNewBatch(context));
          }

          return Column(
            children: [
              _BatchHeader(batch: state.currentBatch!),
              Expanded(
                child: _PageGrid(
                  pages: state.pages,
                  onRemove: (pageId) => context
                      .read<CaptureBatchBloc>()
                      .add(CaptureBatchPageRemoved(pageId: pageId)),
                ),
              ),
              _BottomToolbar(status: state.status),
            ],
          );
        },
      ),
      floatingActionButton: FloatingActionButton.extended(
        onPressed: () => _simulateCapture(context),
        icon: const Icon(Icons.camera_alt_outlined),
        label: const Text('扫描下一页'),
      ),
    );
  }

  void _startNewBatch(BuildContext context) {
    context.read<CaptureBatchBloc>().add(
          const CaptureBatchNewSessionRequested(
            context: CaptureContext.invoiceVoucher,
          ),
        );
  }

  void _simulateCapture(BuildContext context) {
    final fakeBytes = Uint8List(0);
    context.read<CaptureBatchBloc>().add(
          CaptureBatchPageCaptured(
            bytes: fakeBytes,
            contentType: 'image/jpeg',
          ),
        );
  }
}

class _EmptyPlaceholder extends StatelessWidget {
  const _EmptyPlaceholder({required this.onNewBatch});

  final VoidCallback onNewBatch;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          const Icon(Icons.document_scanner_outlined, size: 48),
          const SizedBox(height: 12),
          const Text('开始新的扫描批次'),
          const SizedBox(height: 12),
          FilledButton(
            onPressed: onNewBatch,
            child: const Text('新建批次'),
          ),
        ],
      ),
    );
  }
}

class _BatchHeader extends StatelessWidget {
  const _BatchHeader({required this.batch});

  final CaptureBatch batch;

  @override
  Widget build(BuildContext context) {
    return ListTile(
      leading: const Icon(Icons.folder_open),
      title: Text('批次 ${batch.batchId.substring(0, 8)}'),
      subtitle: Text('状态：${batch.status.name}'),
      trailing: FilledButton(
        onPressed: () =>
            context.read<CaptureBatchBloc>().add(const CaptureBatchSubmitted()),
        child: const Text('提交处理'),
      ),
    );
  }
}

class _PageGrid extends StatelessWidget {
  const _PageGrid({required this.pages, required this.onRemove});

  final List<CapturedPageRef> pages;
  final void Function(String pageId) onRemove;

  @override
  Widget build(BuildContext context) {
    if (pages.isEmpty) {
      return const Center(child: Text('请继续扫描发票页。'));
    }

    return GridView.builder(
      padding: const EdgeInsets.all(16),
      gridDelegate: const SliverGridDelegateWithFixedCrossAxisCount(
        crossAxisCount: 2,
        mainAxisSpacing: 12,
        crossAxisSpacing: 12,
        childAspectRatio: 3 / 4,
      ),
      itemCount: pages.length,
      itemBuilder: (context, index) {
        final page = pages[index];
        return Card(
          clipBehavior: Clip.antiAlias,
          child: Stack(
            fit: StackFit.expand,
            children: [
              Container(
                color: Colors.grey.shade200,
                alignment: Alignment.center,
                child: Text('Page ${page.pageNumber}'),
              ),
              Positioned(
                top: 8,
                right: 8,
                child: IconButton.filledTonal(
                  onPressed: () => onRemove(page.pageId),
                  icon: const Icon(Icons.close),
                ),
              ),
            ],
          ),
        );
      },
    );
  }
}

class _BottomToolbar extends StatelessWidget {
  const _BottomToolbar({required this.status});

  final CaptureBatchUiStatus status;

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
      decoration: BoxDecoration(
        color: Theme.of(context).colorScheme.surfaceContainerHigh,
        border: Border(top: BorderSide(color: Colors.grey.shade300)),
      ),
      child: Row(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: [
          Text('当前状态：${status.name}'),
          if (status == CaptureBatchUiStatus.uploading)
            const CircularProgressIndicator(),
        ],
      ),
    );
  }
}

