module FSWalkthrough.SampleWorkflows.Login

open System
open System.Net.Http
open FSWalkthrough.Core
open FSWalkthrough.Http

type LoginResponse = { Token: string; UserId: string }

type LoginRequest =
    { Username : IFieldValue<string>
      Password : IFieldValue<string> }
    interface WorkflowRequest<LoginResponse>
    static member Default =
        { Username = FieldValues.generated (fun () -> $"user-{Guid.NewGuid():N}@test.com")
          Password = FieldValues.constant "Password123!" }

type LoginStep() =
    inherit HttpStep<LoginRequest, LoginResponse>()
    override _.Method = HttpMethod.Post
    override _.Path   = "/auth/login"
