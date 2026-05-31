module FSWalkthrough.SampleWorkflows.Echo

open System.Collections.Generic
open System.Net.Http
open FSWalkthrough.Core
open FSWalkthrough.Http

type EchoHeadersRequest() =
    interface WorkflowRequest<Dictionary<string, string>>
    static member Default = EchoHeadersRequest()

type EchoHeadersStep() =
    inherit HttpStep<EchoHeadersRequest, Dictionary<string, string>>()
    override _.Method = HttpMethod.Get
    override _.Path   = "/echo/headers"

type EchoHeadersWithStepHeaderRequest() =
    interface WorkflowRequest<Dictionary<string, string>>
    static member Default = EchoHeadersWithStepHeaderRequest()

type EchoHeadersWithStepHeaderStep() =
    inherit HttpStep<EchoHeadersWithStepHeaderRequest, Dictionary<string, string>>()
    override _.Method = HttpMethod.Get
    override _.Path   = "/echo/headers"
    override _.MapHeaders(_) = Dictionary(dict ["x-step-header", "from-step"])
