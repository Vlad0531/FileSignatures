﻿using FileSignaturesConsoleApp.FileReaders;
using FileSignaturesConsoleApp.Helpers;
using FileSignaturesConsoleApp.Entities;
using FileSignaturesConsoleApp.Loggers;

namespace FileSignaturesConsoleApp
{
    public class FileSignatureProcessor
    {
        private readonly IHashGenerator _hashGenerator;
        private readonly IFileReader _fileReader;
        private readonly IBufferedLogger _logger;
        private readonly Mutex _segmentsQueueMutex;
        private readonly Mutex _availableWorkerThreadsMutex;
        private readonly QueueSettings _queueSettings;

        private CancellationTokenSource CanelationTokenSource { get; set; }

        private Queue<Thread> AvailableWorkerThreads { get; set; }

        private Dictionary<int, WorkerThread> WorkerThreads { get; set; }

        private Queue<Segment> SegmentsQueue { get; set; }

        private bool FileIsRead { get; set; }
        private bool ProcessedSucessfully { get; set; }

        //TODO create Queue settings with queue size and coolDown
        public FileSignatureProcessor(IHashGenerator hashGenerator, IFileReader fileReader, IBufferedLogger logger, QueueSettings queueSettings)
        {
            _hashGenerator = hashGenerator;
            _fileReader = fileReader;
            _segmentsQueueMutex = new Mutex(false);
            _availableWorkerThreadsMutex = new Mutex(false);
            _logger = logger;
            _queueSettings = queueSettings;
        }

        public bool ShowFileSignatures(int workerThreadsCount)
        {
            this.ProcessedSucessfully = true;
            this.SegmentsQueue = new Queue<Segment>();
            this.FileIsRead = false;
            var finishedWorkEventHandles = StartWorkerThreads(workerThreadsCount);

            var readerThread = new Thread(() =>
            {
                try
                {
                    var segments = _fileReader.Read(this.CanelationTokenSource.Token);

                    foreach(var segment in segments)
                    {
                        ProcessSegment(segment);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Exception during reading the file!", ex);
                    this.CanelationTokenSource.Cancel();
                    this.ProcessedSucessfully = false;
                }
            });

            readerThread.Priority = ThreadPriority.AboveNormal;
            readerThread.Start();
            _logger.LogImportant($"Thread {readerThread.ManagedThreadId} STARTED reading the file");
            readerThread.Join();
            this.FileIsRead = true;
            _logger.LogImportant($"Thread {readerThread.ManagedThreadId} FINISHED reading the file");
            WaitHandle.WaitAll(finishedWorkEventHandles);
            _logger.WriteBufferedLogs();
            _logger.LogImportant($"All worker threads HAVE FINISHED processing data");
            return this.ProcessedSucessfully;
        }

        private EventWaitHandle[] StartWorkerThreads(int workerThreadsCount)
        {
            this.WorkerThreads = new Dictionary<int, WorkerThread>(workerThreadsCount);
            this.AvailableWorkerThreads = new Queue<Thread>(workerThreadsCount);
            this.CanelationTokenSource = new CancellationTokenSource();
            var finishedWorkEventHandles = new EventWaitHandle[workerThreadsCount];

            for (int i = 0; i < workerThreadsCount; i++)
            {
                var finishedWorkEventHandle = new ManualResetEvent(false);
                finishedWorkEventHandles[i] = finishedWorkEventHandle;

                var thread = new Thread(() =>
                {
                    try
                    {
                        DisplaySegmentHash();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Exception during segment processing by work thread {Environment.CurrentManagedThreadId}!", ex);
                        this.CanelationTokenSource.Cancel();
                        this.ProcessedSucessfully = false;
                    }
                    finally
                    {
                        finishedWorkEventHandle.Set();
                    }
                });

                var ewh = new EventWaitHandle(false, EventResetMode.AutoReset);
                var workerThread = new WorkerThread(thread, ewh);
                this.WorkerThreads.Add(thread.ManagedThreadId, workerThread);
                this.AvailableWorkerThreads.Enqueue(thread);
                thread.Start();
            }

            return finishedWorkEventHandles;
        }

        private void ProcessSegment(Segment segment)
        {
            _availableWorkerThreadsMutex.WaitOne();
            var IsWorkerThreadAvailable = this.AvailableWorkerThreads.Any();

            if (IsWorkerThreadAvailable)
            {
                var availableThread = this.AvailableWorkerThreads.Dequeue();
                ScheduleWork(availableThread.ManagedThreadId, segment);
            }

            //can't use if-else, bc of a possible dead lock
            _availableWorkerThreadsMutex.ReleaseMutex();
            bool tooManyInTheQueue = false;

            if(!IsWorkerThreadAvailable)
            {
                _segmentsQueueMutex.WaitOne();
                this.SegmentsQueue.Enqueue(segment);
                tooManyInTheQueue = _queueSettings.MaxQueueSize > 0 && _queueSettings.MaxQueueSize <= this.SegmentsQueue.Count;
                _segmentsQueueMutex.ReleaseMutex();
            }

            if (tooManyInTheQueue && _queueSettings.CooldownTime > 0)
            {
                Thread.Sleep(_queueSettings.CooldownTime);
            }
        }

        private void DisplaySegmentHash()
        {
            var threadId = Thread.CurrentThread.ManagedThreadId;
            _logger.LogImportant($"Thread {threadId} STARTED working");
            var workerThread = GetWorkerThread(threadId);
            var cancelationToken = this.CanelationTokenSource.Token;
            var finished = false;

            while (!finished)
            {
                var handleIndex = WaitHandle.WaitAny(new[] { workerThread.EventWaitHandle, cancelationToken.WaitHandle });

                if (cancelationToken.IsCancellationRequested)
                {
                    finished = true;
                }
                else
                {
                    var segment = workerThread.Segment;

                    if(segment == null)
                    {
                        throw new InvalidOperationException("No segment has been set. Operation started before segment has been set");
                    }

                    workerThread.Segment = null;
                    var hash = _hashGenerator.GenerateHash(segment.Data);
                    _logger.LogInfo($"Thread: {threadId}, Segment: {segment.Id}, Hash: {Convert.ToHexString(hash)}");
                    ScheduleWorkToCurrentThread();
                }
            }

            _logger.LogImportant($"Thread {threadId} FINISHED working");
        }

        private void ScheduleWorkToCurrentThread()
        {
            _segmentsQueueMutex.WaitOne();
            var queueIsEmpty = !this.SegmentsQueue.Any();

            if (!queueIsEmpty)
            {
                var segment = this.SegmentsQueue.Dequeue();
                ScheduleWork(Environment.CurrentManagedThreadId, segment);
            }

            //can't use if else, bc of a possible dead lock
            _segmentsQueueMutex.ReleaseMutex();

            if (queueIsEmpty)
            {
                if (this.FileIsRead)
                {
                    this.CanelationTokenSource.Cancel();
                }
                else
                {
                    _availableWorkerThreadsMutex.WaitOne();
                    this.AvailableWorkerThreads.Enqueue(Thread.CurrentThread);
                    _availableWorkerThreadsMutex.ReleaseMutex();
                }
            }
        }

        private void ScheduleWork(int threadId, Segment segment)
        {
            var workerThread = GetWorkerThread(threadId);
            workerThread.Segment = segment;
            workerThread.EventWaitHandle.Set();
        }

        private WorkerThread GetWorkerThread(int threadId)
        {
            if (!this.WorkerThreads.ContainsKey(threadId))
            {
                throw new InvalidOperationException($"Unexpected thread Id: {threadId}, Name: {Thread.CurrentThread.Name}. Only registered worker threads are allowed!");
            }

            var workerThread = this.WorkerThreads[threadId];
            return workerThread;
        }
    }
}
