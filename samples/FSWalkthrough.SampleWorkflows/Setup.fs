module FSWalkthrough.SampleWorkflows.Setup

open FSWalkthrough.Core
open FSWalkthrough.Http
open FSWalkthrough.SampleWorkflows.Login
open FSWalkthrough.SampleWorkflows.User
open FSWalkthrough.SampleWorkflows.Order
open FSWalkthrough.SampleWorkflows.Echo

let [<Literal>] private SampleApiUrl = "http://localhost:4200"

// Replaces the C# WalkthroughTestBase constructor.
// Tests call makeRunner() to get a configured WorkflowRunner,
// then run their workflow CE against it.
let makeRunner () =
    let context = WorkflowContext()

    let loginTarget =
        HttpTarget(SampleApiUrl)
            .Register<LoginStep>()

    let apiTarget =
        HttpTarget(SampleApiUrl)
            .Register<CreateUserStep>()
            .Register<UpdateUserAddressStep>()
            .Register<GetUsersByRoleStep>()
            .Register<CreateOrderStep>()
            .Register<GetOrderStep>()
            .Register<EchoHeadersStep>()
            .Register<EchoHeadersWithStepHeaderStep>()
            .WithHeaders(
                ["Authorization",
                 FieldValues.from (fun ctx ->
                    ctx.GetOrDefault<LoginResponse>("LoginRequest")
                    |> Option.map (fun r -> $"Bearer {r.Token}")
                    |> Option.defaultValue "")])

    WorkflowRunner(
        context,
        fun key ->
            if loginTarget.CanHandle(key) then loginTarget :> ITarget
            else apiTarget :> ITarget)
