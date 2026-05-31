# FSWalkthrough — Development Guide

---

## Running tests

Always use `./test.sh`. It starts the sample API, runs all test projects, and tears the API down. Do not run `dotnet test` directly — integration tests depend on the API being up.

Run tests after every change to verify nothing is broken.

---

## Publishing

Use `./publish-local.sh [patch|minor|major|x.y.z]` to bump the version, pack both projects, and write `.nupkg` files to `./nupkgs`. Version is the single source of truth in `src/Directory.Build.props`.

---

## Architecture

```
FSWalkthrough.Core
├── WorkflowRequest<'TResponse>    — marker interface; implemented by request records
├── BuildableRequest               — non-generic marker; constraint for GetAccumulated<'T>
├── BuildableRequest<'TResponse>   — generic marker; implemented by buildable records
├── WorkflowContext                — state bag: captures (by type name) and accumulations (by AccumulationKey)
├── IFieldValue<'T>                — interface for resolvable field values
├── FieldValues                    — constant / generated / from factories
├── FieldValueResolver             — reflection-based resolver; handles nested IFieldValue properties
├── ITarget                        — execute a request; CanHandle(key) for dispatch
├── IRawTarget                     — optional interface for raw (status code + body) execution
├── WorkflowRunner                 — routes to targets, captures responses, orchestrates polling and building
├── Workflow<'T>                   — reader: WorkflowRunner -> Task<'T>
├── WorkflowBuilder                — CE builder; Bind/Yield overloads for Workflow<'T> and #WorkflowRequest<'T>
└── Workflow module                — build / raw / poll / pollWith / run combinators

FSWalkthrough.Http
├── HttpTarget : ITarget           — sends requests over HTTP; steps registered via Register<TStep>()
├── HttpExecutor                   — instance-based HTTP transport; TrySendAsync / SendAsync / SendRawAsync
├── HttpSendResult                 — IsSuccess, StatusCode, Body, IsTransient
├── HttpStepException              — thrown on HTTP failure; carries StatusCode
├── HttpRawResult                  — StatusCode + Body for raw execution
└── HttpStep<'TRequest, 'TResponse> — abstract base; override Method, Path, MapBody, MapQuery, MapHeaders
```

Sample structure:

```
samples/FSWalkthrough.SampleWorkflows/
├── Requests/
│   ├── Login.fs
│   ├── User.fs
│   ├── Echo.fs
│   └── Order.fs
├── Setup.fs
└── Tests.fs
```

---

## Request types

Request records implement `WorkflowRequest<'TResponse>` and carry a `static member Default`:

```fsharp
type CreateUserRequest =
    { Email     : IFieldValue<string>
      FirstName : IFieldValue<string> }
    interface WorkflowRequest<UserResponse>
    static member Default =
        { Email     = FieldValues.constant "test@example.com"
          FirstName = FieldValues.constant "Test" }
```

Buildable records additionally implement `BuildableRequest<'TResponse>` and declare `static member AccumulationKey`:

```fsharp
type AddOrderItem =
    { ProductName : IFieldValue<string>
      Quantity    : IFieldValue<int> }
    interface BuildableRequest<AddOrderItemResponse>
    static member AccumulationKey = typeof<AddOrderItem>
    static member Default = { ProductName = FieldValues.constant "Widget"; Quantity = FieldValues.constant 1 }
```

For polymorphic accumulation, multiple buildable types point their `AccumulationKey` at a shared marker interface:

```fsharp
type OrderLineItem = inherit BuildableRequest   // marker

type PhysicalItem = { ... }
    interface BuildableRequest<AddOrderItemResponse>
    static member AccumulationKey = typeof<OrderLineItem>   // groups with DigitalItem
```

---

## CE syntax

```fsharp
workflow {
    // bare expression — yield and discard result
    LoginRequest.Default
    { CreateUserRequest.Default with Role = FieldValues.constant "admin" }

    // bind — capture result
    let! user = CreateUserRequest.Default

    // buildable — must wrap with build
    build AddOrderItem.Default
    let! item = build { AddOrderItem.Default with ProductName = FieldValues.constant "Widget" }

    // raw — get status code + body without throwing on non-2xx
    let! raw = raw CreateOrderRequest.Default
    let result = raw :?> HttpRawResult

    // poll — retry until predicate
    let! order = pollWith 100 5000 GetOrderRequest.Default (fun r -> r.Status = "pending")

    // async assertion returning Task<'T>
    let! items = fetchOrderItemsAsync order.Id
    Assert.Equal(2, items.Length)

    // async assertion returning Task (unit)
    do! Assert.ThrowsAsync<NotFoundException>(fun () -> getDeletedOrder order.Id)
} |> run runner
```

`build` is required for all `BuildableRequest` items. It is an inline SRTP function that reads `static member AccumulationKey` at compile time and returns `Workflow<'TResponse>`.

---

## Field values

```fsharp
FieldValues.constant "value"                    // fixed value
FieldValues.generated (fun () -> Guid.NewGuid().ToString())   // called fresh each resolution
FieldValues.from (fun ctx -> ctx.Get<UserResponse>("CreateUserRequest").Id)  // from prior capture
```

`ctx.Get<T>(key)` retrieves a captured response by the request type name. `ctx.GetAccumulated<'TItem>()` drains and returns all items accumulated under `typeof<'TItem>`.

---

## HttpStep

```fsharp
type CreateUserStep() =
    inherit HttpStep<CreateUserRequest, UserResponse>()
    override _.Method = HttpMethod.Post
    override _.Path   = "/users"
    // optional overrides: MapBody, MapQuery, MapHeaders
```

Path parameters are extracted automatically from `{placeholderName}` segments. Override `MapBody` to control the exact JSON shape sent to the server.

---

## Consumer docs

The published Claude guidance lives in `docs/claude/` and is copied into consuming projects on package restore via `buildTransitive` in the Core package. Edit those files when the public API or recommended patterns change.

- `docs/claude/claude.md` — consumer entrypoint: library overview and philosophy
- `docs/claude/fsharp-style.md` — F# API patterns (WorkflowRunner, HttpTarget, request/step types, field values)

---

## WorkflowRunner

```fsharp
// Single target
let runner = WorkflowRunner(WorkflowContext(), [| myTarget :> ITarget |])

// Multiple targets — first CanHandle match wins
let runner = WorkflowRunner(WorkflowContext(), [| authTarget :> ITarget; apiTarget :> ITarget |])

// Custom resolver
let runner = WorkflowRunner(WorkflowContext(), fun key ->
    if key = "LoginRequest" then authTarget :> ITarget else apiTarget :> ITarget)
```
