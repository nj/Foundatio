﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Foundatio.Serializer;
using Foundatio.Utility;
using Nito.AsyncEx;

namespace Foundatio.Queues {
    public class InMemoryQueue<T> : QueueBase<T> where T : class {
        private readonly ConcurrentQueue<QueueEntry<T>> _queue = new ConcurrentQueue<QueueEntry<T>>();
        private readonly ConcurrentDictionary<string, QueueEntry<T>> _dequeued = new ConcurrentDictionary<string, QueueEntry<T>>();
        private readonly ConcurrentQueue<QueueEntry<T>> _deadletterQueue = new ConcurrentQueue<QueueEntry<T>>();
        private readonly AsyncAutoResetEvent _autoResetEvent = new AsyncAutoResetEvent();
        private readonly TimeSpan _workItemTimeout = TimeSpan.FromMinutes(10);
        private readonly TimeSpan _retryDelay = TimeSpan.FromMinutes(1);
        private readonly int[] _retryMultipliers = { 1, 3, 5, 10 };
        private readonly int _retries;

        private int _enqueuedCount;
        private int _dequeuedCount;
        private int _completedCount;
        private int _abandonedCount;
        private int _workerErrorCount;
        private int _workerItemTimeoutCount;
        private readonly CancellationTokenSource _queueDisposedCancellationTokenSource;

        public InMemoryQueue(int retries = 2, TimeSpan? retryDelay = null, int[] retryMultipliers = null, TimeSpan? workItemTimeout = null, ISerializer serializer = null, IEnumerable<IQueueBehavior<T>> behaviors = null, ILoggerFactory loggerFactory = null) : base(serializer, behaviors, loggerFactory) {
            _retries = retries;
            if (retryDelay.HasValue)
                _retryDelay = retryDelay.Value;
            if (retryMultipliers != null)
                _retryMultipliers = retryMultipliers;
            if (workItemTimeout.HasValue)
                _workItemTimeout = workItemTimeout.Value;

            InitializeMaintenance();
            _queueDisposedCancellationTokenSource = new CancellationTokenSource();
        }

        protected override Task EnsureQueueCreatedAsync(CancellationToken cancellationToken = new CancellationToken()) {
            return Task.CompletedTask;
        }

        protected override Task<QueueStats> GetQueueStatsImplAsync() {
            return Task.FromResult(new QueueStats {
                Queued = _queue.Count,
                Working = _dequeued.Count,
                Deadletter = _deadletterQueue.Count,
                Enqueued = _enqueuedCount,
                Dequeued = _dequeuedCount,
                Completed = _completedCount,
                Abandoned = _abandonedCount,
                Errors = _workerErrorCount,
                Timeouts = _workerItemTimeoutCount
            });
        }

        protected override async Task<string> EnqueueImplAsync(T data) {
            string id = Guid.NewGuid().ToString("N");
            _logger.Trace("Queue {0} enqueue item: {1}", _queueName, id);

            if (!await OnEnqueuingAsync(data).AnyContext())
                return null;

            var entry = new QueueEntry<T>(id, data.Copy(), this, SystemClock.UtcNow, 0);
            _queue.Enqueue(entry);
            _logger.Trace("Enqueue: Set Event");

            _autoResetEvent.Set();
            Interlocked.Increment(ref _enqueuedCount);

            await OnEnqueuedAsync(entry).AnyContext();
            _logger.Trace("Enqueue done");

            return id;
        }

        protected override void StartWorkingImpl(Func<IQueueEntry<T>, CancellationToken, Task> handler, bool autoComplete, CancellationToken cancellationToken) {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            _logger.Trace("Queue {0} start working", _queueName);

            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;

            Task.Run(async () => {
                _logger.Trace("WorkerLoop Start {0}", _queueName);

                while (!linkedCancellationToken.IsCancellationRequested) {
                    _logger.Trace("WorkerLoop Signaled {0}", _queueName);

                    IQueueEntry<T> queueEntry = null;
                    try {
                        queueEntry = await DequeueImplAsync(cancellationToken: cancellationToken).AnyContext();
                    } catch (Exception ex) {
                        _logger.Error(ex, "Error on Dequeue: " + ex.Message);
                    }

                    if (linkedCancellationToken.IsCancellationRequested || queueEntry == null)
                        return;

                    try {
                        await handler(queueEntry, linkedCancellationToken).AnyContext();
                        if (autoComplete && !queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.CompleteAsync().AnyContext();
                    } catch (Exception ex) {
                        _logger.Error(ex, "Worker error: {0}", ex.Message);
                        if (!queueEntry.IsAbandoned && !queueEntry.IsCompleted)
                            await queueEntry.AbandonAsync().AnyContext();

                        Interlocked.Increment(ref _workerErrorCount);
                    }
                }

                _logger.Trace("Worker exiting: {0} Cancel Requested: {1}", _queueName, linkedCancellationToken.IsCancellationRequested);
            }, linkedCancellationToken);
        }

        protected override async Task<IQueueEntry<T>> DequeueImplAsync(CancellationToken cancellationToken) {
            _logger.Trace("Queue {type} dequeuing item...", _queueName);
            _logger.Trace("Queue count: {0}", _queue.Count);

            var linkedCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(_queueDisposedCancellationTokenSource.Token, cancellationToken).Token;
            while (_queue.Count == 0 && !linkedCancellationToken.IsCancellationRequested) {
                _logger.Trace("Waiting to dequeue item...");
                var sw = Stopwatch.StartNew();

                try {
                    await _autoResetEvent.WaitAsync(cancellationToken).AnyContext();
                } catch (OperationCanceledException) { }

                sw.Stop();
                _logger.Trace("Waited for dequeue: {0}", sw.Elapsed.ToString());
            }

            if (_queue.Count == 0)
                return null;

            _logger.Trace("Dequeue: Attempt");

            QueueEntry<T> info;
            if (!_queue.TryDequeue(out info) || info == null)
                return null;

            info.Attempts++;
            info.DequeuedTimeUtc = SystemClock.UtcNow;

            if (!_dequeued.TryAdd(info.Id, info))
                throw new Exception("Unable to add item to the dequeued list.");

            Interlocked.Increment(ref _dequeuedCount);
            _logger.Trace("Dequeue: Got Item");

            var entry = new QueueEntry<T>(info.Id, info.Value.Copy(), this, info.EnqueuedTimeUtc, info.Attempts);
            await OnDequeuedAsync(entry).AnyContext();
            ScheduleNextMaintenance(SystemClock.UtcNow.Add(_workItemTimeout));

            return entry;
        }

        public override Task RenewLockAsync(IQueueEntry<T> entry) {
            _logger.Trace("Queue {0} renew lock item: {1}", _queueName, entry.Id);
            var item = entry as QueueEntry<T>;

            _dequeued.AddOrUpdate(entry.Id, item, (key, value) => {
                if (item != null)
                    value.RenewedTimeUtc = item.RenewedTimeUtc;

                return value;
            });

            return OnLockRenewedAsync(entry);
        }

        public override async Task CompleteAsync(IQueueEntry<T> entry) {
            _logger.Trace("Queue {0} complete item: {1}", _queueName, entry.Id);

            QueueEntry<T> info;
            if (!_dequeued.TryRemove(entry.Id, out info) || info == null)
                throw new Exception("Unable to remove item from the dequeued list.");

            Interlocked.Increment(ref _completedCount);
            await OnCompletedAsync(entry).AnyContext();
            _logger.Trace("Complete done: {0}", entry.Id);
        }

        public override async Task AbandonAsync(IQueueEntry<T> entry) {
            _logger.Trace("Queue {0} abandon item: {1}", _queueName, entry.Id);

            QueueEntry<T> info;
            if (!_dequeued.TryRemove(entry.Id, out info) || info == null)
                throw new Exception("Unable to remove item from the dequeued list.");

            if (info.Attempts < _retries + 1) {
                if (_retryDelay > TimeSpan.Zero) {
                    _logger.Trace("Adding item to wait list for future retry: {0}", entry.Id);
                    var unawaited = Run.DelayedAsync(GetRetryDelay(info.Attempts), () => RetryAsync(info));
                } else {
                    _logger.Trace("Adding item back to queue for retry: {0}", entry.Id);
                    var unawaited = Task.Run(() => RetryAsync(info));
                }
            } else {
                _logger.Trace("Exceeded retry limit moving to deadletter: {0}", entry.Id);
                _deadletterQueue.Enqueue(info);
            }

            Interlocked.Increment(ref _abandonedCount);
            await OnAbandonedAsync(entry).AnyContext();
            _logger.Trace("Abandon complete: {0}", entry.Id);
        }

        private Task RetryAsync(QueueEntry<T> entry) {
            _logger.Trace("Queue {0} retrying item: {1} Attempts: {2}", _queueName, entry.Id, entry.Attempts);
            _queue.Enqueue(entry);
            _autoResetEvent.Set();
            return Task.CompletedTask;
        }

        private TimeSpan GetRetryDelay(int attempts) {
            int maxMultiplier = _retryMultipliers.Length > 0 ? _retryMultipliers.Last() : 1;
            int multiplier = attempts <= _retryMultipliers.Length ? _retryMultipliers[attempts - 1] : maxMultiplier;
            return TimeSpan.FromMilliseconds((int)(_retryDelay.TotalMilliseconds * multiplier));
        }

        protected override Task<IEnumerable<T>> GetDeadletterItemsImplAsync(CancellationToken cancellationToken) {
            return Task.FromResult(_deadletterQueue.Select(i => i.Value));
        }

        public override Task DeleteQueueAsync() {
            _logger.Trace("Deleting queue: {type}", _queueName);
            _queue.Clear();
            _deadletterQueue.Clear();
            _dequeued.Clear();
            _enqueuedCount = 0;
            _dequeuedCount = 0;
            _completedCount = 0;
            _abandonedCount = 0;
            _workerErrorCount = 0;

            return Task.CompletedTask;
        }
        
        protected override async Task<DateTime?> DoMaintenanceAsync() {
            DateTime utcNow = SystemClock.UtcNow;
            DateTime minAbandonAt = DateTime.MaxValue;

            try {
                foreach (var entry in _dequeued.Values.ToList()) {
                    var abandonAt = entry.RenewedTimeUtc.Add(_workItemTimeout);
                    if (abandonAt < utcNow) {
                        _logger.Info("DoMaintenance Abandon: {entryId}", entry.Id);

                        await AbandonAsync(entry).AnyContext();
                        Interlocked.Increment(ref _workerItemTimeoutCount);
                    } else if (abandonAt < minAbandonAt)
                        minAbandonAt = abandonAt;
                }
            } catch (Exception ex) {
                _logger.Error(ex, "DoMaintenance Error: " + ex.Message);
            }

            return minAbandonAt;
        }

        public override void Dispose() {
            _queueDisposedCancellationTokenSource?.Cancel();
            base.Dispose();
            _queue.Clear();
            _deadletterQueue.Clear();
            _dequeued.Clear();
        }
    }
}