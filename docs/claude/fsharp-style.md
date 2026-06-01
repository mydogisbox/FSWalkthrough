# F# style

_Current version: 1.0.4._

---

## Field value factories

Three factories are available in `FSWalkthrough.Core.FieldValues`:

- `constant value` — returns the same value every time. The value is captured once at construction.
- `generated factory` — invokes the factory each time the field is resolved. Use for values that must be unique per resolution or per test run.
- `from selector` — reads from the context at resolution time. Use for values that come from a prior step's captured response.
- `fromTask factory` — like `from`, but the selector returns `Task<'T>`. Use when the value requires an async lookup.

```fsharp
Id      = FieldValues.generated (fun () -> Guid.NewGuid().ToString())
Email   = FieldValues.generated (fun () -> $"user-{Guid.NewGuid():N}@example.com")
Token   = FieldValues.from (fun ctx -> ctx.Get<LoginResponse>("LoginRequest").Token)
BaseUrl = FieldValues.constant "http://localhost:5020"
OrgId   = FieldValues.fromTask (fun ctx -> task { return! lookupOrgAsync ctx })
```

---

## Captures

Responses are captured under the request type name (`request.GetType().Name`). Reference them with a string literal matching the type name:

```fsharp
ctx.Get<LoginResponse>("LoginRequest")
ctx.Get<UserResponse>("CreateUserRequest")
ctx.Get<OrderResponse>("CreateOrderRequest")
```

`GetOrDefault` returns `Some value` or `None` if the step hasn't run — useful in `from` lambdas where a prior step is optional:

```fsharp
Authorization = FieldValues.from (fun ctx ->
    match ctx.GetOrDefault<LoginResponse>("LoginRequest") with
    | Some r -> $"Bearer {r.Token}"
    | None   -> "")
```

---

## Field value rule

Properties on request and buildable records that can be overridden must be `IFieldValue<'T>`. A raw value bypasses resolution and cannot participate in the field value system:

```fsharp
// Wrong — looks overridable but bypasses resolution
{ CommandType : string }

// Right
{ CommandType : IFieldValue<string> }
```

A field that is intentionally fixed — a discriminator that should never change — can be omitted from the request record and hardcoded in `MapBody` on the step instead.

---

## Request file layout

Response type, request record, and step class live in one file per API concept:

```fsharp
// Order.fs
type OrderResponse     = { Id: string; UserId: string; Items: OrderItemResponse list; Status: string }
type AddOrderItemResponse = { ProductName: string; Quantity: int; UnitPrice: decimal }

type AddOrderItem =
    { ProductName : IFieldValue<string>
      Quantity    : IFieldValue<int>
      UnitPrice   : IFieldValue<decimal> }
    interface BuildableRequest<AddOrderItemResponse>
    static member AccumulationKey = typeof<AddOrderItem>
    static member Default =
        { ProductName = FieldValues.constant "Widget"
          Quantity    = FieldValues.constant 1
          UnitPrice   = FieldValues.constant 9.99m }

type CreateOrderRequest =
    { UserId : IFieldValue<string>
      Items  : IFieldValue<ResizeArray<obj>> }
    interface WorkflowRequest<OrderResponse>
    static member Default =
        { UserId = FieldValues.from (fun ctx -> ctx.Get<UserResponse>("CreateUserRequest").Id)
          Items  = FieldValues.from (fun ctx -> ctx.GetAccumulated<AddOrderItem>()) }

type CreateOrderStep() =
    inherit HttpStep<CreateOrderRequest, OrderResponse>()
    override _.Method = HttpMethod.Post
    override _.Path   = "/orders"
    // MapBody is optional — the default passes all resolved fields through, excluding any whose
    // name matches a {placeholder} in Path. Override to rename, filter, or transform fields.
```

---

## URL parameters and query parameters

Path parameters are declared on the **request** as `IFieldValue<string>` fields. The step's `Path` contains `{placeholder}` segments — the step auto-extracts values by matching placeholder names to request field names (case-insensitive). Path param fields are automatically excluded from the request body.

```fsharp
type GetOrderRequest =
    { OrderId : IFieldValue<string> }
    interface WorkflowRequest<OrderResponse>
    static member Default =
        { OrderId = FieldValues.from (fun ctx -> ctx.Get<OrderResponse>("CreateOrderRequest").Id) }

type GetOrderStep() =
    inherit HttpStep<GetOrderRequest, OrderResponse>()
    override _.Method = HttpMethod.Get
    override _.Path   = "/orders/{orderId}"   // {orderId} matches OrderId field (case-insensitive)
```

Query parameters are declared via `MapQuery` on the step, which receives the resolved request fields and returns the query string key-value pairs:

```fsharp
type GetUsersByRoleRequest =
    { Role : IFieldValue<string> }
    interface WorkflowRequest<UserResponse list>
    static member Default = { Role = FieldValues.constant "user" }

type GetUsersByRoleStep() =
    inherit HttpStep<GetUsersByRoleRequest, UserResponse list>()
    override _.Method = HttpMethod.Get
    override _.Path   = "/users"
    override _.MapQuery(fields) = Dictionary(dict ["role", fields["Role"].ToString()])
```

Produces: `GET /users?role=user`

---

## CE syntax

The `workflow` computation expression is the primary way to write tests. Each step is either yielded (fire-and-forget) or bound with `let!` to capture the result. The CE is run against a `WorkflowRunner` via `|> run runner`.

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
} |> run runner
```

`build` is required for all `BuildableRequest` items. It reads `static member AccumulationKey` at compile time and returns `Workflow<'TResponse>`.

---

## Awaiting tasks inside the CE

`let!` and `do!` accept `Task<'T>` and `Task` directly. The intended use is async assertions — xUnit and similar frameworks expose async assert helpers that return `Task`:

```fsharp
workflow {
    let! order = CreateOrderRequest.Default

    // async assertion returning Task<'T>
    let! items = fetchOrderItemsAsync order.Id   // Task<Item list>
    Assert.Equal(2, items.Length)

    // async assertion returning Task (unit)
    do! Assert.ThrowsAsync<NotFoundException>(fun () -> getDeletedOrder order.Id)
}
```

The task result is not captured in `WorkflowContext` — use the bound name directly.

---

## Per-invocation overrides

Use F# record copy-and-update (`with`) to override fields per call. This works for any request field:

```fsharp
workflow {
    let! first     = CreateOrderRequest.Default
    let! second    = CreateOrderRequest.Default
    let! retrieved = { GetOrderRequest.Default with OrderId = FieldValues.constant first.Id }
    let! admins    = { GetUsersByRoleRequest.Default with Role = FieldValues.constant "admin" }
    ...
}
```

When updating multiple fields in a multiline `with` expression, continuation fields must start at exactly the same column as the first field:

```fsharp
let item = { AddOrderItem.Default with ProductName = FieldValues.constant "Deluxe Widget"
                                       Quantity    = FieldValues.constant 3 }
```

---

## Test structure

Each `[<Fact>]` builds a `Workflow<unit>` and runs it against a fresh runner:

```fsharp
[<Fact>]
let ``PlacedOrder CanBeRetrieved`` () =
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        build AddOrderItem.Default
        let! created   = CreateOrderRequest.Default
        let! retrieved = GetOrderRequest.Default
        Assert.Equal(created.Id, retrieved.Id)
        Assert.Equal("pending", retrieved.Status)
    } |> run (makeRunner())
```

Each test gets a fresh `WorkflowRunner` (and `WorkflowContext`) with no shared state.

---

## Polling

`pollWith` re-executes a step on an interval until a predicate passes or the timeout is reached:

```fsharp
let! order = pollWith 500 10000 GetOrderRequest.Default (fun r -> r.Status = "shipped")
Assert.Equal("shipped", order.Status)
```

- First arg — interval in ms between attempts (default via `poll`: 500)
- Second arg — timeout in ms before throwing `WorkflowContextException` (default via `poll`: 10000)
- Only the final passing response is captured; intermediate attempts overwrite each other

Use `poll` for the defaults:

```fsharp
let! order = poll GetOrderRequest.Default (fun r -> r.Status = "shipped")
```

---

## Raw execution

`raw` sends the request without throwing on non-2xx responses and returns `Workflow<obj>`. The concrete type for `HttpTarget` is `HttpRawResult`, which carries `StatusCode` and `Body`. Cast accordingly:

```fsharp
workflow {
    LoginRequest.Default
    CreateUserRequest.Default
    build AddOrderItem.Default
    let! boxed  = raw CreateOrderRequest.Default
    let result  = boxed :?> HttpRawResult
    let order   = result.Body :?> OrderResponse
    Assert.Equal(201,       result.StatusCode)
    Assert.Equal("pending", order.Status)
}
```

On non-2xx responses, `Body` is the deserialized `TResponse` when the body matches that shape — otherwise the raw JSON string:

```fsharp
let! boxed  = raw { CreateOrderRequest.Default with UserId = FieldValues.constant "nonexistent-id" }
let result  = boxed :?> HttpRawResult
Assert.Equal(400, result.StatusCode)
```

---

## Custom targets and multi-target routing

`WorkflowRunner` is target-agnostic — it routes each step to whatever `ITarget` the resolver returns, then captures the response. `HttpTarget` is one implementation; any type can implement `ITarget` to wrap an SDK, a raw `HttpClient`, or an in-memory stub.

All captures are shared through the same `WorkflowContext` regardless of which target produced them. A `from` lambda can read captures from any prior step, even one that ran against a different target.

`WorkflowRunner` accepts multiple targets directly. Each request is routed to the first target whose `CanHandle` returns true. `HttpTarget.CanHandle` returns true only for request type names it has a registered step for:

```fsharp
let loginTarget =
    HttpTarget("http://localhost:4200")
        .Register<LoginStep>()

let apiTarget =
    HttpTarget("http://localhost:4200")
        .Register<CreateUserStep>()
        .Register<CreateOrderStep>()
        .WithHeaders(
            ["Authorization",
             FieldValues.from (fun ctx ->
                ctx.Get<LoginResponse>("LoginRequest").Token |> sprintf "Bearer %s")])

let runner = WorkflowRunner(WorkflowContext(), [| loginTarget :> ITarget; apiTarget :> ITarget |])
```

`LoginRequest` is handled by `loginTarget`; everything else falls through to `apiTarget`.

For custom routing logic, pass a resolver function:

```fsharp
let runner =
    WorkflowRunner(WorkflowContext(), fun key ->
        if loginTarget.CanHandle(key) then loginTarget :> ITarget
        else apiTarget :> ITarget)
```

---

## Headers

Override `MapHeaders` on `HttpStep` for step-level headers. It receives the resolved request fields and returns headers to add or override:

```fsharp
type EchoHeadersWithStepHeaderStep() =
    inherit HttpStep<EchoHeadersRequest, Dictionary<string, string>>()
    override _.Method = HttpMethod.Get
    override _.Path   = "/echo/headers"
    override _.MapHeaders(_) = Dictionary(dict ["x-step-header", "from-step"])
```

Supply target-level headers via `WithHeaders`. Use `from` for headers that depend on a prior step's response:

```fsharp
HttpTarget("http://localhost:4200")
    .Register<CreateItemStep>()
    .WithHeaders(
        ["X-Tenant-Id",   FieldValues.constant "acme"
         "Authorization", FieldValues.from (fun ctx ->
            ctx.Get<LoginResponse>("LoginRequest").Token |> sprintf "Bearer %s")])
```

---

## Building an array

`build` resolves all `IFieldValue<'T>` properties immediately, stores the typed `TResponse` in the accumulation, and returns that same snapshot. `ctx.GetAccumulated<'TItem>()` returns `ResizeArray<obj>` — each element is the concrete `TResponse` produced by `build` — and **clears the accumulation**. Resolution happens once at build time, not again when the request is sent.

`build` returns `Workflow<'TResponse>` — a plain record with resolved values, not `IFieldValue<'T>` wrappers:

```fsharp
workflow {
    let! widget = build { AddOrderItem.Default with ProductName = FieldValues.constant "Deluxe Widget"
                                                    Quantity    = FieldValues.constant 3 }
    build AddOrderItem.Default
    let! order = CreateOrderRequest.Default
    Assert.Equal("Deluxe Widget", widget.ProductName)
    Assert.Equal(3, widget.Quantity)
}
```

---

## Accumulating multiple variants

When variants have genuinely different fields, use subtypes pointing their `AccumulationKey` at a shared marker interface:

```fsharp
// Marker — subtypes set AccumulationKey = typeof<OrderLineItem> to group together
type OrderLineItem = inherit BuildableRequest

type PhysicalItem =
    { ProductName     : IFieldValue<string>
      ShippingAddress : IFieldValue<string> }
    interface BuildableRequest<AddOrderItemResponse>
    static member AccumulationKey = typeof<OrderLineItem>
    static member Default = { ProductName = FieldValues.constant "Widget"
                              ShippingAddress = FieldValues.constant "123 Main St" }

type DigitalItem =
    { ProductName : IFieldValue<string>
      DownloadUrl : IFieldValue<string> }
    interface BuildableRequest<AddOrderItemResponse>
    static member AccumulationKey = typeof<OrderLineItem>
    static member Default = { ProductName = FieldValues.constant "E-Book"
                              DownloadUrl = FieldValues.constant "https://example.com/download" }
```

`ctx.GetAccumulated<OrderLineItem>()` retrieves both `PhysicalItem` and `DigitalItem` builds.

---

## Field value resolution

`FieldValueResolver` resolves `IFieldValue<'T>` properties on any request or buildable record. Resolution is recursive: after resolving `IFieldValue<'T>` → `'T`, if `'T` is itself a record with `IFieldValue<'U>` properties, those are resolved too — producing a nested `Dictionary<string, obj>`. List elements are also recursed into.

```fsharp
type RegionFields =
    { State   : IFieldValue<string>
      Country : IFieldValue<string> }
    static member Default = { State = FieldValues.constant "IL"; Country = FieldValues.constant "US" }

type AddressFields =
    { Street : IFieldValue<string>
      City   : IFieldValue<string>
      Region : IFieldValue<RegionFields> }
    static member Default =
        { Street = FieldValues.constant "123 Main St"
          City   = FieldValues.constant "Springfield"
          Region = FieldValues.constant RegionFields.Default }
```

Overriding only what differs — unspecified fields keep their defaults:

```fsharp
let region  = { RegionFields.Default  with State = FieldValues.constant "MA" }
let addr    = { AddressFields.Default with City   = FieldValues.constant "Boston"
                                          Region = FieldValues.constant region }
let! result = { UpdateUserAddressRequest.Default with Address = FieldValues.constant addr }

Assert.Equal("Boston",      result.Contact.Primary.Address.City)
Assert.Equal("123 Main St", result.Contact.Primary.Address.Street)   // default preserved
Assert.Equal("MA",          result.Contact.Primary.Address.Region.State)
Assert.Equal("US",          result.Contact.Primary.Address.Region.Country)  // default preserved
```
