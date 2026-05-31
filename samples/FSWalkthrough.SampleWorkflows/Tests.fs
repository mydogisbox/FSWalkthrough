module FSWalkthrough.SampleWorkflows.Tests

open System.Collections.Generic
open System.Net.Http
open FSWalkthrough.Core
open FSWalkthrough.Core.Workflow
open FSWalkthrough.Http
open FSWalkthrough.SampleWorkflows.Login
open FSWalkthrough.SampleWorkflows.User
open FSWalkthrough.SampleWorkflows.Order
open FSWalkthrough.SampleWorkflows.Echo
open FSWalkthrough.SampleWorkflows.Setup
open Xunit

// ── Helper types defined at module level (F# doesn't allow type defs in function bodies) ──

type ExplicitCreateUserStep() =
    inherit HttpStep<CreateUserRequest, UserResponse>()
    override _.Method = HttpMethod.Post
    override _.Path   = "/users"
    override _.MapBody(fields) =
        Dictionary(dict [
            "Email",     fields["Email"]
            "FirstName", fields["FirstName"]
            "LastName",  fields["LastName"]
            "Role",      fields["Role"] ])

// ── Tests ─────────────────────────────────────────────────────────────────────
//
// Each test builds a Workflow<unit> value, then runs it against a fresh runner.
// Bare request/item expressions are implicitly yielded. `let! x = req` binds a
// result for later use. Default are accessed via Type.Default; overrides use
// F# record with-expressions: { Type.Default with Field = FieldValues.constant v }.

[<Fact>]
let ``NewUser CanPlaceOrder StatusIsPending`` () =
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        build AddOrderItem.Default
        let! order = CreateOrderRequest.Default
        Assert.Equal("pending", order.Status)
        Assert.Single(order.Items) |> box |> ignore
    } |> run (makeRunner())

[<Fact>]
let ``NewUser CanPlaceOrder WithSpecificItems`` () =
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        let item1 = { AddOrderItem.Default with ProductName = FieldValues.constant "Deluxe Widget"
                                                Quantity    = FieldValues.constant 3 }
        let item2 = { AddOrderItem.Default with ProductName = FieldValues.constant "Basic Widget" }
        build item1
        build item2
        let! order = CreateOrderRequest.Default
        Assert.Equal("pending", order.Status)
        Assert.Equal(2, order.Items.Length)
        Assert.Equal("Deluxe Widget", order.Items[0].ProductName)
        Assert.Equal("Basic Widget",  order.Items[1].ProductName)
    } |> run (makeRunner())

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

[<Fact>]
let ``GetOrder UsesPathParam`` () =
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        build AddOrderItem.Default
        let! first  = CreateOrderRequest.Default
        let! second = CreateOrderRequest.Default
        let! retrieved = { GetOrderRequest.Default with OrderId = FieldValues.constant first.Id }
        Assert.Equal(first.Id, retrieved.Id)
        Assert.NotEqual<string>(second.Id, retrieved.Id)
    } |> run (makeRunner())

[<Fact>]
let ``GetUsersByRole UsesQueryParam`` () =
    workflow {
        LoginRequest.Default
        { CreateUserRequest.Default with Role = FieldValues.constant "user" }
        { CreateUserRequest.Default with Role = FieldValues.constant "admin" }
        let! users  = GetUsersByRoleRequest.Default
        let! admins = { GetUsersByRoleRequest.Default with Role = FieldValues.constant "admin" }
        Assert.All(users,  fun u -> Assert.Equal("user",  u.Role))
        Assert.All(admins, fun u -> Assert.Equal("admin", u.Role))
    } |> run (makeRunner())

[<Fact>]
let ``StepHeaders ReceivedByServer`` () =
    workflow {
        let! echo = EchoHeadersWithStepHeaderRequest.Default
        Assert.Equal("from-step", echo["x-step-header"])
    } |> run (makeRunner())

[<Fact>]
let ``TargetAuthHeader ReceivedByServer`` () =
    workflow {
        LoginRequest.Default
        let! echo = EchoHeadersRequest.Default
        Assert.StartsWith("Bearer ", echo["authorization"])
    } |> run (makeRunner())

[<Fact>]
let ``UpdateUserAddress NestedFieldValuesResolvedRecursively`` () =
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        let region  = { RegionFields.Default  with State = FieldValues.constant "MA" }
        let addr    = { AddressFields.Default  with City   = FieldValues.constant "Boston"
                                                    Region = FieldValues.constant region }
        let primary = { PrimaryFields.Default  with Address = FieldValues.constant addr }
        let contact = { ContactFields.Default  with Primary = FieldValues.constant primary }
        let! result = { UpdateUserAddressRequest.Default with Contact = FieldValues.constant contact }
        Assert.Equal("Boston",      result.Contact.Primary.Address.City)
        Assert.Equal("MA",          result.Contact.Primary.Address.Region.State)
        Assert.Equal("123 Main St", result.Contact.Primary.Address.Street)
        Assert.Equal("US",          result.Contact.Primary.Address.Region.Country)
    } |> run (makeRunner())

[<Fact>]
let ``BuildItem ReturnsResolvedResponse`` () =
    workflow {
        let item = { AddOrderItem.Default with ProductName = FieldValues.constant "Deluxe Widget"
                                               Quantity    = FieldValues.constant 3 }
        let! widget = build item
        Assert.Equal("Deluxe Widget", widget.ProductName)
        Assert.Equal(3,               widget.Quantity)
        Assert.Equal(9.99m,           widget.UnitPrice)
    } |> run (makeRunner())

[<Fact>]
let ``CreateOrder StatusCodeAvailableViaRawResult SuccessReturns201`` () =
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        build AddOrderItem.Default
        let! raw   = raw CreateOrderRequest.Default
        let result = raw :?> HttpRawResult
        let order  = result.Body :?> OrderResponse
        Assert.Equal(201,       result.StatusCode)
        Assert.Equal("pending", order.Status)
    } |> run (makeRunner())

[<Fact>]
let ``CreateOrder StatusCodeAvailableViaRawResult UnknownUserReturns400`` () =
    workflow {
        LoginRequest.Default
        let! raw   = raw { CreateOrderRequest.Default with UserId = FieldValues.constant "nonexistent-id" }
        let result = raw :?> HttpRawResult
        Assert.Equal(400, result.StatusCode)
    } |> run (makeRunner())

[<Fact>]
let ``PlacedOrder CanBePolled`` () =
    let authTarget =
        HttpTarget("http://localhost:4200")
            .Register<LoginStep>()
    let apiTarget =
        HttpTarget("http://localhost:4200")
            .Register<CreateUserStep>()
            .Register<CreateOrderStep>()
            .Register<GetOrderStep>()
            .WithHeaders(
                ["Authorization",
                 FieldValues.from (fun ctx ->
                    ctx.Get<LoginResponse>("LoginRequest").Token |> sprintf "Bearer %s")])
    let runner =
        WorkflowRunner(
            WorkflowContext(),
            fun key ->
                if authTarget.CanHandle(key) then authTarget :> ITarget
                else apiTarget :> ITarget)
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        build AddOrderItem.Default
        CreateOrderRequest.Default
        let! order = pollWith 100 5000 GetOrderRequest.Default (fun r -> r.Status = "pending")
        Assert.Equal("pending", order.Status)
        Assert.Single(order.Items) |> box |> ignore
    } |> run runner

[<Fact>]
let ``MixedItemTypes AccumulateUnderBaseType`` () =
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        build { PhysicalItem.Default with ProductName = FieldValues.constant "Physical Widget" }
        build { DigitalItem.Default  with ProductName = FieldValues.constant "Premium E-Book" }
        let! order = { CreateOrderRequest.Default with Items = FieldValues.from (fun ctx -> ctx.GetAccumulated<OrderLineItem>()) }
        Assert.Equal(2,                 order.Items.Length)
        Assert.Equal("Physical Widget", order.Items[0].ProductName)
        Assert.Equal("Premium E-Book",  order.Items[1].ProductName)
    } |> run (makeRunner())

[<Fact>]
let ``MapBody ExplicitFieldMapping WorksCorrectly`` () =
    let context     = WorkflowContext()
    let loginTarget = HttpTarget("http://localhost:4200").Register<LoginStep>()
    let apiTarget   =
        HttpTarget("http://localhost:4200")
            .Register(ExplicitCreateUserStep())
            .WithHeaders(
                ["Authorization",
                 FieldValues.from (fun ctx ->
                    ctx.Get<LoginResponse>("LoginRequest").Token |> sprintf "Bearer %s")])
    let runner =
        WorkflowRunner(context,
            fun key -> if key = "LoginRequest" then loginTarget :> ITarget else apiTarget :> ITarget)
    workflow {
        LoginRequest.Default
        let! user = CreateUserRequest.Default
        Assert.NotEmpty(user.Id)
        Assert.Equal("Test", user.FirstName)
    } |> run runner

[<Fact>]
let ``MultiTarget EachTargetHandlesItsOwnSteps`` () =
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
    workflow {
        LoginRequest.Default
        CreateUserRequest.Default
        build AddOrderItem.Default
        let! order = CreateOrderRequest.Default
        Assert.Equal("pending", order.Status)
        Assert.Single(order.Items) |> box |> ignore
    } |> run runner
