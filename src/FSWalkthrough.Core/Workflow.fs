namespace FSWalkthrough.Core

open System
open System.Threading.Tasks

// ── Workflow type ─────────────────────────────────────────────────────────────
//
// A Workflow<'T> is a reader over WorkflowRunner. The CE threads the runner
// implicitly, replacing the C# base-class pattern where the runner was an
// inherited field and Execute/Build were protected helper methods.

type Workflow<'T> = WorkflowRunner -> Task<'T>

// ── Builder ───────────────────────────────────────────────────────────────────

type WorkflowBuilder() =

    member _.Return(x: 'T) : Workflow<'T> =
        fun _ -> Task.FromResult(x)

    member _.ReturnFrom(wf: Workflow<'T>) : Workflow<'T> = wf

    member _.Zero() : Workflow<unit> =
        fun _ -> Task.FromResult(())

    member _.Bind(wf: Workflow<'T>, f: 'T -> Workflow<'U>) : Workflow<'U> =
        fun runner -> task {
            let! x = wf runner
            return! (f x) runner
        }

    member _.Bind(request: #WorkflowRequest<'T>, f: 'T -> Workflow<'U>) : Workflow<'U> =
        fun runner -> task {
            let! x = runner.ExecuteAsync(request :> WorkflowRequest<'T>)
            return! (f x) runner
        }

    member _.Delay(f: unit -> Workflow<'T>) : Workflow<'T> =
        fun runner -> f () runner

    member _.Yield(request: #WorkflowRequest<'T>) : Workflow<unit> =
        fun runner -> task { let! _ = runner.ExecuteAsync(request :> WorkflowRequest<'T>) in return () }

    member _.Yield(wf: Workflow<'T>) : Workflow<unit> =
        fun runner -> task { let! _ = wf runner in return () }

    member _.Combine(wf1: Workflow<unit>, wf2: Workflow<'T>) : Workflow<'T> =
        fun runner -> task {
            do! wf1 runner
            return! wf2 runner
        }

    member _.TryWith(wf: Workflow<'T>, handler: exn -> Workflow<'T>) : Workflow<'T> =
        fun runner -> task {
            try return! wf runner
            with ex -> return! (handler ex) runner
        }

    member _.TryFinally(wf: Workflow<'T>, finalizer: unit -> unit) : Workflow<'T> =
        fun runner -> task {
            try return! wf runner
            finally finalizer()
        }

    member _.Using(resource: 'TDisp when 'TDisp :> System.IDisposable, body: 'TDisp -> Workflow<'T>) : Workflow<'T> =
        fun runner -> task {
            use r = resource
            return! (body r) runner
        }

[<AutoOpen>]
module WorkflowBuilderInstance =
    let workflow = WorkflowBuilder()

// ── Workflow combinators ──────────────────────────────────────────────────────

module Workflow =

    let inline build< ^TItem, 'TResponse
            when ^TItem :> BuildableRequest<'TResponse>
            and  ^TItem : (static member AccumulationKey : Type)>
            (item: ^TItem) : Workflow<'TResponse> =
        fun runner ->
            runner.BuildAsync(item :> BuildableRequest<'TResponse>, (^TItem : (static member AccumulationKey : Type) ()))

    let raw (request: WorkflowRequest<'TResponse>) : Workflow<obj> =
        fun runner -> runner.ExecuteRawAsync(request)

    let poll (request: WorkflowRequest<'TResponse>) (until: 'TResponse -> bool) : Workflow<'TResponse> =
        fun runner -> runner.PollAsync(request, until)

    let pollWith (intervalMs: int) (timeoutMs: int) (request: WorkflowRequest<'TResponse>) (until: 'TResponse -> bool) : Workflow<'TResponse> =
        fun runner -> runner.PollAsync(request, until, intervalMs = intervalMs, timeoutMs = timeoutMs)

    let run (runner: WorkflowRunner) (wf: Workflow<'T>) : Task<'T> = wf runner
