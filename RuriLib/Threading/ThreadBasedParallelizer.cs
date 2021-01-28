﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RuriLib.Threading
{
    /// <inheritdoc/>
    public class ThreadBasedParallelizer<TInput, TOutput> : Parallelizer<TInput, TOutput>
    {
        #region Private Fields
        public List<Thread> threadPool = new();
        #endregion

        #region Constructors
        /// <inheritdoc/>
        public ThreadBasedParallelizer(IEnumerable<TInput> workItems, Func<TInput, CancellationToken, Task<TOutput>> workFunction,
            int degreeOfParallelism, int totalAmount, int skip = 0) : base(workItems, workFunction, degreeOfParallelism, totalAmount, skip)
        {

        }
        #endregion

        #region Public Methods
        /// <inheritdoc/>
        public async override Task Start()
        {
            await base.Start();

            Status = ParallelizerStatus.Running;
            _ = Task.Run(() => Run()).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async override Task Pause()
        {
            await base.Pause();

            Status = ParallelizerStatus.Pausing;
            await WaitCurrentWorkCompletion();
            Status = ParallelizerStatus.Paused;
        }

        /// <inheritdoc/>
        public async override Task Resume()
        {
            await base.Resume();

            Status = ParallelizerStatus.Running;
        }

        /// <inheritdoc/>
        public async override Task Stop()
        {
            await base.Stop();

            Status = ParallelizerStatus.Stopping;
            softCTS.Cancel();
            await WaitCompletion().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public async override Task Abort()
        {
            await base.Abort();

            Status = ParallelizerStatus.Stopping;
            hardCTS.Cancel();
            softCTS.Cancel();
        }

        /// <inheritdoc/>
        public async override Task ChangeDegreeOfParallelism(int newValue)
        {
            await base.ChangeDegreeOfParallelism(newValue);

            degreeOfParallelism = newValue;
        }
        #endregion

        #region Private Methods
        // Run is executed in fire and forget mode (not awaited)
        private async void Run()
        {
            // Skip the items
            var items = workItems.Skip(skip).GetEnumerator();

            while (items.MoveNext())
            {
                WAIT:

                // If we paused, stay idle
                if (Status == ParallelizerStatus.Pausing || Status == ParallelizerStatus.Paused)
                {
                    await Task.Delay(1000);
                    goto WAIT;
                }

                // If we canceled the loop
                if (softCTS.IsCancellationRequested)
                {
                    break;
                }

                // If we haven't filled the thread pool yet, start a new thread
                // (e.g. if we're at the beginning or the increased the DOP)
                if (threadPool.Count < degreeOfParallelism)
                {
                    StartNewThread(items.Current);
                }
                // Otherwise if we already filled the thread pool
                else
                {
                    // Search for the first idle thread
                    var firstFree = threadPool.FirstOrDefault(t => !t.IsAlive);

                    // If there is none, go back to waiting
                    if (firstFree == null)
                    {
                        await Task.Delay(100);
                        goto WAIT;
                    }

                    // Otherwise remove it
                    threadPool.Remove(firstFree);

                    // If there's space for a new thread, start it
                    if (threadPool.Count < degreeOfParallelism)
                    {
                        StartNewThread(items.Current);
                    }
                    // Otherwise go back to waiting
                    else
                    {
                        await Task.Delay(100);
                        goto WAIT;
                    }
                }
            }

            // Wait until ongoing threads finish
            await WaitCurrentWorkCompletion();

            OnCompleted();
            Status = ParallelizerStatus.Idle;
        }

        // Creates and starts a thread, given a work item
        private void StartNewThread(TInput item)
        {
            var thread = new Thread(new ParameterizedThreadStart(ThreadWork));
            threadPool.Add(thread);
            thread.Start(item);
        }

        // Sync method to be passed to a thread
        private void ThreadWork(object input)
            => taskFunction((TInput)input).Wait();

        // Wait until the current round is over (if we didn't cancel, it's the last one)
        private async Task WaitCurrentWorkCompletion()
        {
            while (threadPool.Any(t => t.IsAlive))
            {
                await Task.Delay(100);
            }
        }
        #endregion
    }
}