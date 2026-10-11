using System.Reflection;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("ChatBranch.Tests.BoundedParallelFramework", "ChatBranch.Tests")]

namespace ChatBranch.Tests;

/// <summary>
/// xUnit's parallel runner, minus its synchronization context.
///
/// xUnit limits parallelism with a SynchronizationContext that owns exactly
/// maxParallelThreads (= cores) threads, and every await in a test resumes on one
/// of them. The API's send seams block on async work (`.GetAwaiter().GetResult()`
/// — see "Thread-pool floor" in CLAUDE.md); a test that reaches one from that
/// context waits for a continuation that needs a context thread. On a 2-core CI
/// runner both threads ended up waiting so — a deadlock that never finished
/// (SQLite's "async" completes synchronously so it never showed; Npgsql's real
/// async I/O does). Production has no SynchronizationContext, so only tests need it.
///
/// Here the collections (test classes) run with NO context — continuations go to
/// the thread pool — and parallelism is bounded by a semaphore instead:
/// at most max(4, 2 × maxParallelThreads) classes in flight, which also stops
/// ~75 API hosts being alive at once.
/// </summary>
public sealed class BoundedParallelFramework(IMessageSink sink) : XunitTestFramework(sink)
{
    protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName) =>
        new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

    private sealed class Executor(AssemblyName name, ISourceInformationProvider sources, IMessageSink sink)
        : XunitTestFrameworkExecutor(name, sources, sink)
    {
        protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink,
            ITestFrameworkExecutionOptions executionOptions)
        {
            using var runner = new Runner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
            await runner.RunAsync();
        }
    }

    private sealed class Runner(ITestAssembly assembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnostics,
        IMessageSink executionMessages, ITestFrameworkExecutionOptions options)
        : XunitTestAssemblyRunner(assembly, testCases, diagnostics, executionMessages, options)
    {
        protected override async Task<RunSummary> RunTestCollectionsAsync(IMessageBus messageBus,
            CancellationTokenSource cancellationTokenSource)
        {
            if (ExecutionOptions.DisableParallelizationOrDefault())
                return await base.RunTestCollectionsAsync(messageBus, cancellationTokenSource);

            var threads = ExecutionOptions.MaxParallelThreadsOrDefault();
            var limit = Math.Max(4, (threads > 0 ? threads : Environment.ProcessorCount) * 2);
            using var gate = new SemaphoreSlim(limit);
            var runs = OrderTestCollections().Select(async collection =>
            {
                await gate.WaitAsync(cancellationTokenSource.Token);
                try
                {
                    // Task.Run: the collection starts on the thread pool, with no context to capture
                    return await Task.Run(() => RunTestCollectionAsync(messageBus, collection.Item1, collection.Item2,
                        cancellationTokenSource), cancellationTokenSource.Token);
                }
                finally { gate.Release(); }
            }).ToList();

            var summary = new RunSummary();
            foreach (var one in await Task.WhenAll(runs)) summary.Aggregate(one);
            return summary;
        }
    }
}
