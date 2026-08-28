# FSWalkthrough

Write API integration tests as multi-step workflows in F#.

A test should read as something a consumer of your system can do — log in, create a user, place an order — not as a sequence of HTTP calls. FSWalkthrough gives you a computation expression for exactly that, with defaults covering everything a given test doesn't care about.

```fsharp
[<Fact>]
let ``NewUser CanPlaceOrder`` () =
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        build AddOrderItem.Default
        let! order = CreateOrderRequest.Default
        Assert.Equal("pending", order.Status)
    } |> run (makeRunner ())
```

Every field a test names is a claim that the field matters to that test. Defaults are what keep that claim honest.

## Packages

| Package | Contents |
| --- | --- |
| `mciccotti.FSWalkthrough.Core` | Request and response types, the `workflow` CE, `WorkflowRunner`, field values |
| `mciccotti.FSWalkthrough.Http` | `HttpTarget` and `HttpStep` — sends workflow requests over HTTP |

```bash
dotnet add package mciccotti.FSWalkthrough.Http
```

`mciccotti.FSWalkthrough.Http` depends on `mciccotti.FSWalkthrough.Core`, so for HTTP workflows that one package is enough. Both target `net10.0`.

Package IDs carry the `mciccotti.` prefix; the namespaces do not. Install `mciccotti.FSWalkthrough.Http`, then `open FSWalkthrough.Http`.

## Requests and steps

A request record describes the inputs to one step and the response it produces. `Default` supplies a value for every field:

```fsharp
type CreateUserRequest =
    { Email     : IFieldValue<string>
      FirstName : IFieldValue<string> }
    interface WorkflowRequest<UserResponse>
    static member Default =
        { Email     = FieldValues.generated (fun () -> $"user-{Guid.NewGuid():N}@test.com")
          FirstName = FieldValues.constant "Test" }
```

A step binds that request to a transport:

```fsharp
type CreateUserStep() =
    inherit HttpStep<CreateUserRequest, UserResponse>()
    override _.Method = HttpMethod.Post
    override _.Path   = "/users"
```

Path parameters are filled from `{placeholder}` segments automatically. Override `MapBody`, `MapQuery`, or `MapHeaders` when the wire shape differs from the record.

## Field values

```fsharp
FieldValues.constant  "value"
FieldValues.generated (fun () -> Guid.NewGuid().ToString())
FieldValues.from      (fun ctx -> ctx.Get<UserResponse>("CreateUserRequest").Id)
```

`from` is how a later step reuses an earlier response. Referencing a prior value rather than hardcoding one keeps the test honest: it says *this depends on that*, not *this particular string matters*.

## Running a workflow

Register steps on a target, then hand a runner to `run`:

```fsharp
let makeRunner () =
    let api =
        HttpTarget("http://localhost:4200")
            .Register<CreateUserStep>()
            .Register<CreateOrderStep>()

    WorkflowRunner(WorkflowContext(), [| api :> ITarget |])
```

Multiple targets dispatch by `CanHandle`, first match wins — which is how login and API calls can use different base URLs or carry different headers.

## Inside the CE

```fsharp
workflow {
    // run and discard the result
    LoginRequest.Default

    // run and bind the result
    let! user = CreateUserRequest.Default

    // override a default
    { CreateUserRequest.Default with Role = FieldValues.constant "admin" }

    // accumulate array items, then the request that consumes them
    build AddOrderItem.Default
    let! order = CreateOrderRequest.Default

    // retry until a predicate holds
    let! settled = pollWith 100 5000 GetOrderRequest.Default (fun r -> r.Status = "settled")

    // status code and body, without throwing on non-2xx
    let! attempt = raw CreateOrderRequest.Default
} |> run (makeRunner ())
```

`for` and `while` work inside the CE, and `let!` / `do!` accept any `Task`, so assertions that query the database directly sit inline with the rest of the workflow.

`build` is required for every `BuildableRequest`. It reads `static member AccumulationKey` at compile time via SRTP, which is what lets several item types accumulate into one collection.

## Guidance for coding agents

`mciccotti.FSWalkthrough.Core` ships its own usage and style guides inside the package. On build they are written into your project:

```
.claude/fswalkthrough/
├── claude.md         — library overview and testing philosophy
└── fsharp-style.md   — F# API patterns and conventions
```

To put them in front of an agent, import the entrypoint from your `CLAUDE.md`:

```
@import .claude/fswalkthrough/claude.md
```

The files are rewritten whenever the installed package version changes, so the guidance an agent reads always matches the version you actually have. Treat `.claude/fswalkthrough/` as generated — edits there are overwritten on the next build.

## License

MIT
