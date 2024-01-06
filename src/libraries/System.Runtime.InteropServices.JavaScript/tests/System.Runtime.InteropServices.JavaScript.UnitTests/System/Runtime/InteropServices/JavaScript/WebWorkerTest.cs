﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Http;
using Xunit;
using System.IO;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Linq;

namespace System.Runtime.InteropServices.JavaScript.Tests
{
    // TODO test:
    // JSWebWorker.RunAsync with CancellationToken
    // JSExport 2x
    // JSExport async
    // timer
    // GC + finalizer + dispose
    // lock
    // thread allocation, many threads
    // TLS
    // ProxyContext flow, child thread, child task
    // use JSObject after JSWebWorker finished, especially HTTP
    // JSWebWorker JS setTimeout till after close
    // WS on JSWebWorker
    // Yield will hit event loop 3x
    // HTTP continue on TP
    // WS continue on TP
    // event pipe
    // FS
    // unit test for problem **7)**

    public class WebWorkerTest
    {
        #region executor threads

        public static IEnumerable<object[]> GetTargetThreads()
        {
            return Enum.GetValues<ExecutorType>().Select(type => new object[] { new Executor(type) });
        }

        public static IEnumerable<object[]> GetSpecificTargetThreads()
        {
            yield return new object[] { new Executor(ExecutorType.JSWebWorker), new Executor(ExecutorType.Main) };
            yield break;
        }

        public static IEnumerable<object[]> GetTargetThreads2x()
        {
            return Enum.GetValues<ExecutorType>().SelectMany(
                type1 => Enum.GetValues<ExecutorType>().Select(
                    type2 => new object[] { new Executor(type1), new Executor(type2) }));
        }

        #endregion

        #region Console, Yield, Delay

        [Theory, MemberData(nameof(GetTargetThreads))]
        public async Task ManagedConsole(Executor executor)
        {
            await executor.Execute(() =>
            {
                Console.WriteLine("C# Hello from ManagedThreadId: " + Environment.CurrentManagedThreadId);
                return Task.CompletedTask;
            });
        }

        [Theory, MemberData(nameof(GetTargetThreads))]
        public async Task JSConsole(Executor executor)
        {
            await executor.Execute(() =>
            {
                WebWorkerTestHelper.Log("JS Hello from ManagedThreadId: " + Environment.CurrentManagedThreadId + " NativeThreadId: " + WebWorkerTestHelper.NativeThreadId);
                return Task.CompletedTask;
            });
        }

        [Theory, MemberData(nameof(GetTargetThreads))]
        public async Task NativeThreadId(Executor executor)
        {
            await executor.Execute(async () =>
            {
                await executor.StickyAwait(WebWorkerTestHelper.InitializeAsync());

                var jsTid = WebWorkerTestHelper.GetTid();
                var csTid = WebWorkerTestHelper.NativeThreadId;
                if (executor.Type == ExecutorType.Main || executor.Type == ExecutorType.JSWebWorker)
                {
                    Assert.Equal(jsTid, csTid);
                }
                else
                {
                    Assert.NotEqual(jsTid, csTid);
                }

                await WebWorkerTestHelper.DisposeAsync();
            });
        }

        [Theory, MemberData(nameof(GetTargetThreads))]
        public async Task ThreadingTimer(Executor executor)
        {
            await executor.Execute(async () =>
            {
                TaskCompletionSource tcs = new TaskCompletionSource();
                executor.AssertTargetThread();

                using var timer = new Threading.Timer(_ =>
                {
                    Assert.NotEqual(1, Environment.CurrentManagedThreadId);
                    Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                    tcs.SetResult();
                }, null, 100, Timeout.Infinite);

                await tcs.Task;
            });
        }

        [Theory, MemberData(nameof(GetTargetThreads))]
        public async Task JSDelay_ContinueWith(Executor executor)
        {
            await executor.Execute(async () =>
            {
                await executor.StickyAwait(WebWorkerTestHelper.CreateDelay());

                await WebWorkerTestHelper.Delay(1).ContinueWith(_ =>
                {
                    // continue on the context of the target JS interop
                    executor.AssertInteropThread();
                }, TaskContinuationOptions.ExecuteSynchronously);
            });
        }

        [Theory, MemberData(nameof(GetTargetThreads))]
        public async Task JSDelay_ConfigureAwait_True(Executor executor)
        {
            await executor.Execute(async () =>
            {
                await executor.StickyAwait(WebWorkerTestHelper.CreateDelay());

                await WebWorkerTestHelper.Delay(1).ConfigureAwait(true);

                executor.AssertAwaitCapturedContext();
            });
        }

        [Theory, MemberData(nameof(GetTargetThreads))]
        [ActiveIssue("https://github.com/dotnet/runtime/issues/96493")]
        public async Task ManagedDelay_ContinueWith(Executor executor)
        {
            await executor.Execute(async () =>
            {
                executor.AssertTargetThread();
                await Task.Delay(10).ContinueWith(_ =>
                {
                    // continue on the context of the Timer's thread pool thread
                    Assert.True(Thread.CurrentThread.IsThreadPoolThread);
                }, TaskContinuationOptions.ExecuteSynchronously);
            });
        }


        [Theory, MemberData(nameof(GetTargetThreads))]
        public async Task ManagedDelay_ConfigureAwait_True(Executor executor)
        {
            await executor.Execute(async () =>
            {
                executor.AssertTargetThread();

                await Task.Delay(1).ConfigureAwait(true);

                executor.AssertAwaitCapturedContext();
            });
        }

        [Theory, MemberData(nameof(GetTargetThreads))]
        public async Task ManagedYield(Executor executor)
        {
            await executor.Execute(async () =>
            {
                executor.AssertTargetThread();

                await Task.Yield();

                executor.AssertAwaitCapturedContext();
            });
        }

        #endregion

        #region affinity

        private async Task ActionsInDifferentThreads<T>(Executor executor1, Executor executor2, Func<Task, TaskCompletionSource<T>, Task> e1Job, Func<T, Task> e2Job)
        {
            TaskCompletionSource<T> readyTCS = new TaskCompletionSource<T>();
            TaskCompletionSource doneTCS = new TaskCompletionSource();

            var e1 = executor1.Execute(async () =>
            {
                await e1Job(doneTCS.Task, readyTCS);
                if (!readyTCS.Task.IsCompleted)
                {
                    readyTCS.SetResult(default);
                }
                await doneTCS.Task;
            });

            var r1 = await readyTCS.Task.ConfigureAwait(true);

            var e2 = executor2.Execute(async () =>
            {

                executor2.AssertTargetThread();

                await e2Job(r1);

                doneTCS.SetResult();
            });

            await e2;
            await e1;
        }

        [Theory, MemberData(nameof(GetTargetThreads2x))]
        public async Task JSObject_CapturesAffinity(Executor executor1, Executor executor2)
        {
            var e1Job = async (Task e2done, TaskCompletionSource<JSObject> e1State) =>
            {
                await WebWorkerTestHelper.InitializeAsync();

                executor1.AssertAwaitCapturedContext();

                var jsState = await WebWorkerTestHelper.PromiseState();

                // share the state with the E2 continuation
                e1State.SetResult(jsState);

                await e2done;

                // cleanup
                await WebWorkerTestHelper.DisposeAsync();
            };

            var e2Job = async (JSObject e1State) =>
            {
                bool valid = await WebWorkerTestHelper.PromiseValidateState(e1State);
                Assert.True(valid);
            };

            await ActionsInDifferentThreads<JSObject>(executor1, executor2, e1Job, e2Job);
        }

        #endregion
    }
}
