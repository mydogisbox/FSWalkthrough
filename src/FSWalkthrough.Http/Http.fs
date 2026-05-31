namespace FSWalkthrough.Http

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open System.Threading.Tasks
open FSWalkthrough.Core

// ── Result / Exception types ──────────────────────────────────────────────────

type HttpSendResult =
    { IsSuccess: bool; StatusCode: int; Body: string; IsTransient: bool }

type HttpStepException(message: string, statusCode: int) =
    inherit Exception(message)
    new(message) = HttpStepException(message, 0)
    member _.StatusCode = statusCode

type HttpRawResult = { StatusCode: int; Body: obj }

// ── Executor ──────────────────────────────────────────────────────────────────

type HttpExecutor(baseUrl: string) =

    static let serializeOptions =
        JsonSerializerOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

    static let deserializeOptions =
        let opts = JsonSerializerOptions(PropertyNameCaseInsensitive = true)
        opts.Converters.Add(JsonFSharpConverter())
        opts

    static let sharedClient =
        new HttpClient(new HttpClientHandler(AllowAutoRedirect = false))

    let trimmedBase = baseUrl.TrimEnd('/')

    static member SerializeOptions   = serializeOptions
    static member DeserializeOptions = deserializeOptions

    static member Deserialize<'T>(json: string) : 'T =
        let result = JsonSerializer.Deserialize<'T>(json, deserializeOptions)
        if isNull (box result) then
            raise (HttpStepException($"Response deserialized to null for type '{typeof<'T>.Name}'."))
        result

    member private _.SendCoreAsync
            (method      : HttpMethod,
             path        : string,
             pathParams  : Dictionary<string, obj>,
             queryParams : Dictionary<string, string>,
             bodyFields  : Dictionary<string, obj>,
             headers     : Dictionary<string, string>)
            : Task<HttpResponseMessage> =
        let mutable p = path
        for kv in pathParams do
            p <- p.Replace(
                    $"{{{kv.Key}}}",
                    Uri.EscapeDataString(if isNull kv.Value then "" else kv.Value.ToString()),
                    StringComparison.OrdinalIgnoreCase)
        if queryParams.Count > 0 then
            let qs = String.Join("&", queryParams |> Seq.map (fun kv ->
                $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"))
            p <- p.TrimEnd([|'?';'&'|]) + (if p.Contains('?') then "&" else "?") + qs
        let url = trimmedBase + "/" + p.TrimStart('/')
        let req = new HttpRequestMessage(method, url)
        if method <> HttpMethod.Get && method <> HttpMethod.Delete && bodyFields.Count > 0 then
            let json    = JsonSerializer.Serialize(bodyFields :> obj, serializeOptions)
            req.Content <- new StringContent(json, Encoding.UTF8, "application/json")
        for kv in headers do
            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value) |> ignore
        sharedClient.SendAsync(req)

    member this.TrySendAsync
            (method      : HttpMethod,
             path        : string,
             pathParams  : Dictionary<string, obj>,
             queryParams : Dictionary<string, string>,
             bodyFields  : Dictionary<string, obj>,
             headers     : Dictionary<string, string>)
            : Task<HttpSendResult> =
        task {
            try
                let! response  = this.SendCoreAsync(method, path, pathParams, queryParams, bodyFields, headers)
                let! body      = response.Content.ReadAsStringAsync()
                let code       = int response.StatusCode
                let transient  = code = 503 || code = 504 || code = 429 || code = 404
                return { IsSuccess = response.IsSuccessStatusCode; StatusCode = code; Body = body; IsTransient = transient }
            with
            | :? HttpRequestException as ex ->
                return { IsSuccess = false; StatusCode = 0; Body = ex.Message; IsTransient = true }
        }

    member this.SendAsync
            (method      : HttpMethod,
             path        : string,
             pathParams  : Dictionary<string, obj>,
             queryParams : Dictionary<string, string>,
             bodyFields  : Dictionary<string, obj>,
             headers     : Dictionary<string, string>)
            : Task<string> =
        task {
            let! result = this.TrySendAsync(method, path, pathParams, queryParams, bodyFields, headers)
            if not result.IsSuccess then
                if result.StatusCode = 0 then raise (HttpRequestException(result.Body))
                raise (HttpStepException(
                    $"HTTP {method} {trimmedBase}/{path.TrimStart('/')} failed with {result.StatusCode}. Body: {result.Body}",
                    result.StatusCode))
            return result.Body
        }

    member this.SendRawAsync
            (method      : HttpMethod,
             path        : string,
             pathParams  : Dictionary<string, obj>,
             queryParams : Dictionary<string, string>,
             bodyFields  : Dictionary<string, obj>,
             headers     : Dictionary<string, string>)
            : Task<int * string> =
        task {
            let! result = this.TrySendAsync(method, path, pathParams, queryParams, bodyFields, headers)
            return (result.StatusCode, result.Body)
        }

// ── HttpStep ──────────────────────────────────────────────────────────────────

[<AbstractClass>]
type HttpStep() =
    static let placeholderRegex =
        Regex(@"\{(\w+)\}", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

    static member PlaceholderRegex = placeholderRegex

    abstract RequestType : Type
    abstract MapBody    : Dictionary<string, obj> -> Dictionary<string, obj>
    default _.MapBody(fields) = fields
    abstract MapQuery   : Dictionary<string, obj> -> Dictionary<string, string>
    default _.MapQuery(_)     = Dictionary()
    abstract MapHeaders : Dictionary<string, obj> -> Dictionary<string, string>
    default _.MapHeaders(_)   = Dictionary()

    // Dispatch hooks called by HttpTarget — return boxed TResponse and HttpRawResult respectively.
    abstract RunStepAsync    : HttpExecutor * Dictionary<string, obj> * Dictionary<string, string> -> Task<obj>
    abstract RunRawStepAsync : HttpExecutor * Dictionary<string, obj> * Dictionary<string, string> -> Task<obj>

    interface IStep with
        member this.RequestType = this.RequestType

[<AbstractClass>]
type HttpStep<'TRequest, 'TResponse when 'TRequest :> WorkflowRequest<'TResponse>>() =
    inherit HttpStep()

    override _.RequestType = typeof<'TRequest>

    abstract Method : HttpMethod
    abstract Path   : string

    member private this.PrepareRequest
            (resolvedFields : Dictionary<string, obj>,
             targetHeaders  : Dictionary<string, string>) =
        let pathParamNames = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let pathParams     = Dictionary<string, obj>(StringComparer.OrdinalIgnoreCase)
        for m in HttpStep.PlaceholderRegex.Matches(this.Path) do
            let name = m.Groups[1].Value
            pathParamNames.Add(name) |> ignore
            match resolvedFields.Keys |> Seq.tryFind (fun k -> k.Equals(name, StringComparison.OrdinalIgnoreCase)) with
            | Some field -> pathParams[name] <- resolvedFields[field]
            | None -> ()
        let query   = this.MapQuery(resolvedFields)
        let headers = Dictionary<string, string>(targetHeaders, StringComparer.OrdinalIgnoreCase)
        for kv in this.MapHeaders(resolvedFields) do headers[kv.Key] <- kv.Value
        let body =
            this.MapBody(resolvedFields)
            |> Seq.filter (fun kv -> not (pathParamNames.Contains kv.Key))
            |> Seq.map    (fun kv -> KeyValuePair(kv.Key, kv.Value))
            |> Dictionary
        (pathParams, query, headers, body)

    override this.RunStepAsync(executor, resolvedFields, targetHeaders) =
        task {
            let (pp, q, h, b) = this.PrepareRequest(resolvedFields, targetHeaders)
            let! json     = executor.SendAsync(this.Method, this.Path, pp, q, b, h)
            return HttpExecutor.Deserialize<'TResponse>(json) :> obj
        }

    override this.RunRawStepAsync(executor, resolvedFields, targetHeaders) =
        task {
            let (pp, q, h, b) = this.PrepareRequest(resolvedFields, targetHeaders)
            let! (statusCode, json) = executor.SendRawAsync(this.Method, this.Path, pp, q, b, h)
            let body : obj =
                if String.IsNullOrEmpty(json) then null
                else
                    try HttpExecutor.Deserialize<'TResponse>(json) :> obj
                    with _ -> json :> obj
            return { StatusCode = statusCode; Body = body } :> obj
        }

// ── HttpTarget ────────────────────────────────────────────────────────────────

type HttpTarget(baseUrl: string) =

    let steps    = Dictionary<string, HttpStep>(StringComparer.OrdinalIgnoreCase)
    let executor = HttpExecutor(baseUrl)
    let mutable headers = Dictionary<string, IFieldValue<string>>()

    member _.CanHandle(key: string) = steps.ContainsKey(key)

    member this.Register(step: HttpStep) : HttpTarget =
        steps[step.RequestType.Name] <- step
        this

    member this.Register<'TStep when 'TStep :> HttpStep and 'TStep : (new : unit -> 'TStep)>() : HttpTarget =
        this.Register(new 'TStep())

    member this.WithHeaders(hdrs: (string * IFieldValue<string>) seq) : HttpTarget =
        headers <- Dictionary<string, IFieldValue<string>>(hdrs |> Seq.map KeyValuePair)
        this

    member private _.GetStep<'TResponse>(request: WorkflowRequest<'TResponse>) : HttpStep =
        match steps.TryGetValue(request.GetType().Name) with
        | true, step -> step
        | _ -> invalidOp $"No step registered for '{request.GetType().Name}'."

    interface ITarget with
        member this.CanHandle(key) = this.CanHandle(key)

        member this.ExecuteAsync<'TResponse>(request, resolvedFields, ctx) =
            task {
                let step          = this.GetStep(request)
                let targetHeaders = FieldValueResolver.resolveGroup headers ctx
                let! boxed        = step.RunStepAsync(executor, resolvedFields, targetHeaders)
                return boxed :?> 'TResponse
            }

    interface IRawTarget with
        member this.ExecuteRawAsync<'TResponse>(request: WorkflowRequest<'TResponse>, resolvedFields, ctx) =
            task {
                let step          = this.GetStep(request)
                let targetHeaders = FieldValueResolver.resolveGroup headers ctx
                return! step.RunRawStepAsync(executor, resolvedFields, targetHeaders)
            }
