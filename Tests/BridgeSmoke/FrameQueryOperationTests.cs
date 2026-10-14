using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using GraphQL;
using Newtonsoft.Json.Linq;

namespace Neko.RimWorld;

internal static class FrameQueryOperationTests
{
    internal static void Run()
    {
        var iterator = new Steps(new[] { 0, 0, 0 }, delay: 4);
        using (var operation = new Probe(iterator)) {
            var frames = 0;
            while (!operation.Advance()) frames++;
            Require(frames == 3 && operation.Restarts == 4 && operation.Validations == 4,
                "A delayed native step bypassed cooperative frame boundaries or slice restart");
            Require(operation.Completions == 1 && operation.Errors == 0 && iterator.Disposals == 1,
                "Completion did not finalize and release exactly once");
            operation.Advance(); operation.Dispose();
            Require(operation.Completions == 1 && iterator.Moves == 4 && iterator.Disposals == 1 && operation.Releases == 1,
                "Repeated Advance/Dispose restarted a completed read");
        }

        iterator = new Steps(new[] { 1, 0 });
        using (var operation = new Probe(iterator) { SpatialYield = true }) {
            Require(!operation.Advance() && iterator.Moves == 1 && operation.Completions == 0,
                "Spatial step 1 did not yield before the next native step");
            Require(operation.Advance() && operation.Completions == 1 && iterator.Disposals == 1,
                "Spatial explicit yield prevented later completion");
        }

        foreach (var failure in new Exception[] {
            ReadContext.Error("world_changed", "fixture world changed"),
            ReadContext.Error("deadline_exceeded", "fixture slice exceeded"),
            new InvalidOperationException("fixture unavailable"),
        }) {
            iterator = new Steps(new[] { 0 }) { Failure = failure };
            using var operation = new Probe(iterator);
            Require(operation.Advance() && ReferenceEquals(operation.LastError, failure) && operation.Errors == 1 &&
                operation.Completions == 0 && iterator.Disposals == 1 && operation.Releases == 1,
                "Recoverable failure was replaced, completed as success or leaked iterator ownership");
            Require(operation.Outcome!.Status == "partial" && operation.Outcome.Data! != null,
                "Shared lifecycle changed the domain's partial result");
        }

        iterator = new Steps(new[] { 0 });
        using (var operation = new Probe(iterator) { Expired = true, Reject = true }) {
            Require(operation.Advance() && iterator.Moves == 0 && operation.Errors == 1 && iterator.Disposals == 1,
                "Lifetime failure reached the iterator or leaked its handles");
            Require(operation.Outcome!.Status == "failed" && operation.Outcome.ErrorCode == "fixture_deadline",
                "The lifecycle lost the domain's explicit deadline error");
        }

        iterator = new Steps(new[] { 0 });
        using (var operation = new Probe(iterator)) {
            operation.Dispose(); operation.Dispose();
            Require(operation.Advance() && iterator.Moves == 0 && operation.Validations == 0 && iterator.Disposals == 1,
                "A cancelled operation continued reading or disposed twice");
        }

        iterator = new Steps(Array.Empty<int>());
        using (var operation = new Probe(iterator)) {
            var published = new QueryOutcome { Data = new JObject { ["domain_sample"] = "published-in-iterator" } };
            iterator.Ended = () => operation.Publish(published);
            Require(operation.Advance() && ReferenceEquals(operation.Outcome, published) &&
                operation.Completions == 1 && iterator.Disposals == 1,
                "An iterator-published outcome was replaced or bypassed terminal release");
        }

        iterator = new Steps(new[] { 0 }) { Failure = new InvalidOperationException("fixture read error") };
        using (var operation = new Probe(iterator) { HandlerThrows = true }) {
            try { operation.Advance(); throw new Exception("A failing domain error handler was silently swallowed"); }
            catch (ApplicationException) { }
            Require(operation.Errors == 1 && iterator.Disposals == 1 && operation.Releases == 1,
                "A domain error-handler failure leaked iterator ownership");
        }

        iterator = new Steps(new[] { 0 }) { Failure = new AccessViolationException("managed fatal fixture") };
        using (var operation = new Probe(iterator)) {
            try { operation.Advance(); throw new Exception("Fatal failure was silently recovered"); }
            catch (AccessViolationException) { Require(operation.Errors == 0, "Fatal failure entered domain recovery"); }
        }
        Require(iterator.Disposals == 1, "Explicit cancellation after a fatal failure leaked the iterator");
        Console.WriteLine("Frame lifecycle tests passed: slice/yield boundaries, completion, domain failures, deadline rejection, cancellation and exactly-once release.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }

    private sealed class Probe : FrameQueryOperation
    {
        internal int Restarts, Validations, Completions, Errors, Releases;
        internal bool SpatialYield, Expired, Reject, HandlerThrows;
        internal Exception? LastError;
        internal Probe(IEnumerator<int> steps) => InitializeWork(steps);
        protected override void RestartReadSlice() => Restarts++;
        protected override void ValidateFrame()
        {
            Validations++;
            EnsureLifetime(Expired ? 0 : 8, "fixture_deadline", "fixture lifetime expired");
        }
        protected override bool YieldImmediately(int step) => SpatialYield && step == 1;
        internal void Publish(QueryOutcome outcome) => Outcome=outcome;
        protected override void Complete()
        { Completions++; Outcome ??= new QueryOutcome { Data = new JObject { ["domain_sample"] = "complete" } }; }
        protected override void HandleError(Exception error)
        {
            Errors++; LastError = error;
            if (HandlerThrows) throw new ApplicationException("fixture handler error");
            Outcome = Reject ? QueryOutcome.Failure((error as ExecutionError)?.Code ?? "fixture_failed", error.Message)
                : QueryOutcome.Partial(new JObject { ["domain_sample"] = "partial" }, "fixture_partial", error.Message);
        }
        protected override void ReleaseReferences() => Releases++;
    }

    private sealed class Steps : IEnumerator<int>
    {
        private readonly int[] values;
        private readonly int delay;
        private int index = -1;
        internal int Moves, Disposals;
        internal Exception? Failure;
        internal Action? Ended;
        internal Steps(int[] values, int delay = 0) { this.values = values; this.delay = delay; }
        public int Current => values[index];
        object IEnumerator.Current => Current;
        public bool MoveNext()
        {
            Moves++;
            if (Failure != null) throw Failure;
            if (++index >= values.Length) { Ended?.Invoke(); return false; }
            if (delay != 0) Thread.Sleep(delay);
            return true;
        }
        public void Reset() => throw new NotSupportedException();
        public void Dispose() => Disposals++;
    }
}
