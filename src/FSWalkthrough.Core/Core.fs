namespace FSWalkthrough.Core

open System
open System.Collections.Generic
open System.Reflection
open System.Text.Json
open System.Threading.Tasks

// ── Exceptions ────────────────────────────────────────────────────────────────

type WorkflowContextException(message: string) =
    inherit Exception(message)

// ── Request base types ────────────────────────────────────────────────────────

type WorkflowRequest<'TResponse> = interface end

// Non-generic marker — satisfies the GetAccumulated constraint
type BuildableRequest = interface end

type BuildableRequest<'TResponse> =
    inherit BuildableRequest

// ── Context ───────────────────────────────────────────────────────────────────

type WorkflowContext() =
    let captures    = Dictionary<string, obj>()
    let accumulated = Dictionary<Type, ResizeArray<obj>>()

    member _.Accumulate(key: Type, response: obj) =
        if not (accumulated.ContainsKey(key)) then
            accumulated[key] <- ResizeArray()
        accumulated[key].Add(response)

    member _.GetAccumulated<'TItem when 'TItem :> BuildableRequest>() : ResizeArray<obj> =
        match accumulated.TryGetValue(typeof<'TItem>) with
        | true, list ->
            accumulated.Remove(typeof<'TItem>) |> ignore
            list
        | _ -> ResizeArray()

    member _.Get<'T>(key: string) : 'T =
        match captures.TryGetValue(key) with
        | false, _ ->
            let available = String.Join(", ", captures.Keys)
            raise (WorkflowContextException(
                $"No captured response found for '{key}'. Available keys: [{available}]"))
        | true, value ->
            match value with
            | :? 'T as typed -> typed
            | _ ->
                raise (WorkflowContextException(
                    $"Captured response for '{key}' is of type '{value.GetType().Name}', not '{typeof<'T>.Name}'."))

    member _.GetOrDefault<'T>(key: string) : 'T option =
        match captures.TryGetValue(key) with
        | true, (:? 'T as typed) -> Some typed
        | _ -> None

    member _.GetRaw(key: string) : obj =
        match captures.TryGetValue(key) with
        | false, _ ->
            let available = String.Join(", ", captures.Keys)
            raise (WorkflowContextException(
                $"No captured response found for '{key}'. Available keys: [{available}]"))
        | true, value -> value

    member _.CaptureRaw(key: string, response: obj) =
        captures[key] <- response

// ── Field values ──────────────────────────────────────────────────────────────

type IFieldValue<'T> =
    abstract ResolveAsync : WorkflowContext -> Task<'T>

module FieldValues =
    type private StaticValue<'T>(value: 'T) =
        interface IFieldValue<'T> with
            member _.ResolveAsync(_) = Task.FromResult(value)

    type private GeneratedValue<'T>(generator: unit -> 'T) =
        interface IFieldValue<'T> with
            member _.ResolveAsync(_) = Task.FromResult(generator())

    type private FromValue<'T>(selector: WorkflowContext -> 'T) =
        interface IFieldValue<'T> with
            member _.ResolveAsync(ctx) = Task.FromResult(selector ctx)

    type private FromTaskValue<'T>(factory: WorkflowContext -> Task<'T>) =
        interface IFieldValue<'T> with
            member _.ResolveAsync(ctx) = factory ctx

    let constant<'T> (value: 'T) : IFieldValue<'T>              = StaticValue(value) :> _
    let generated<'T> (generator: unit -> 'T) : IFieldValue<'T> = GeneratedValue(generator) :> _
    let from<'T> (selector: WorkflowContext -> 'T) : IFieldValue<'T> = FromValue(selector) :> _
    let fromTask<'T> (factory: WorkflowContext -> Task<'T>) : IFieldValue<'T> = FromTaskValue(factory) :> _

// ── Step / Target interfaces ──────────────────────────────────────────────────

type IStep =
    abstract RequestType : Type

type StepError = { Message: string; IsTransient: bool }

type ITarget =
    abstract ExecuteAsync<'TResponse> :
        WorkflowRequest<'TResponse> * Dictionary<string, obj> * WorkflowContext -> Task<'TResponse>
    abstract CanHandle : string -> bool

type IRawTarget =
    abstract ExecuteRawAsync<'TResponse> :
        WorkflowRequest<'TResponse> * Dictionary<string, obj> * WorkflowContext -> Task<obj>

// ── Field value resolver ──────────────────────────────────────────────────────

module FieldValueResolver =
    let private fieldValueOpenGeneric = typedefof<IFieldValue<_>>

    let private getFieldValueInterface (propType: Type) =
        if propType.IsGenericType && propType.GetGenericTypeDefinition() = fieldValueOpenGeneric then
            Some propType
        else
            propType.GetInterfaces()
            |> Array.tryFind (fun i ->
                i.IsGenericType && i.GetGenericTypeDefinition() = fieldValueOpenGeneric)

    let private awaitBoxedTask (taskObj: obj) : Task<obj> =
        task {
            let t = taskObj :?> Task
            do! t
            return t.GetType().GetProperty("Result").GetValue(t)
        }

    let rec private resolveRecursivelyAsync (value: obj) (context: WorkflowContext) : Task<obj> =
        task {
            if isNull value then
                return null
            else
                let t = value.GetType()
                if t.IsPrimitive || value :? string || value :? decimal || t.IsEnum then
                    return value
                elif value :? System.Collections.IList then
                    let list   = value :?> System.Collections.IList
                    let result = ResizeArray(list.Count)
                    for item in Seq.cast<obj> list do
                        let! resolved = resolveRecursivelyAsync item context
                        result.Add(resolved)
                    return result :> obj
                else
                    let props = t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                    if props |> Array.exists (fun p -> getFieldValueInterface p.PropertyType |> Option.isSome) then
                        let! dict = resolvePropertiesAsync value context Set.empty (fun _ -> false)
                        return dict :> obj
                    else
                        return value
        }

    and private resolvePropertiesAsync
            (target: obj)
            (context: WorkflowContext)
            (excludedNames: Set<string>)
            (isBaseType: Type -> bool)
            : Task<Dictionary<string, obj>> =
        task {
            let result = Dictionary<string, obj>()
            let props =
                target.GetType().GetProperties(BindingFlags.Public ||| BindingFlags.Instance)
                |> Array.filter (fun p -> not (excludedNames.Contains p.Name))
                |> Array.filter (fun p -> not (isBaseType p.DeclaringType))
            for prop in props do
                let propValue = prop.GetValue(target)
                if isNull propValue then
                    result[prop.Name] <- null
                else
                    match getFieldValueInterface prop.PropertyType with
                    | Some iface ->
                        let m = iface.GetMethod("ResolveAsync")
                        let taskObj = m.Invoke(propValue, [| context |])
                        let! resolved = awaitBoxedTask taskObj
                        let! recResolved = resolveRecursivelyAsync resolved context
                        result[prop.Name] <- recResolved
                    | None ->
                        result[prop.Name] <- propValue
            return result
        }

    let resolveAsync<'TResponse>
            (request: WorkflowRequest<'TResponse>)
            (context: WorkflowContext)
            : Task<Dictionary<string, obj>> =
        resolvePropertiesAsync request context Set.empty (fun _ -> false)

    let resolveObjectAsync (item: BuildableRequest) (context: WorkflowContext) : Task<Dictionary<string, obj>> =
        resolvePropertiesAsync item context Set.empty (fun _ -> false)

    let resolveGroupAsync
            (fields: Dictionary<string, IFieldValue<string>>)
            (context: WorkflowContext)
            : Task<Dictionary<string, string>> =
        task {
            let result = Dictionary<string, string>()
            for kv in fields do
                let! v = kv.Value.ResolveAsync(context)
                result[kv.Key] <- v
            return result
        }

// ── WorkflowRunner ────────────────────────────────────────────────────────────

type WorkflowRunner private (context: WorkflowContext, resolver: (string -> ITarget) voption) =

    static let jsonOptions = JsonSerializerOptions(PropertyNameCaseInsensitive = true)

    new(context: WorkflowContext) =
        WorkflowRunner(context, ValueNone)

    new(context: WorkflowContext, targets: ITarget array) =
        WorkflowRunner(context, ValueSome (fun key ->
            match targets |> Array.tryFind (fun t -> t.CanHandle key) with
            | Some t -> t
            | None ->
                raise (WorkflowContextException($"No target can handle '{key}'."))))

    new(context: WorkflowContext, resolver: string -> ITarget) =
        WorkflowRunner(context, ValueSome resolver)

    member private _.Resolve(key: string) =
        match resolver with
        | ValueSome r -> r key
        | ValueNone ->
            raise (WorkflowContextException(
                "No target resolver registered. Provide targets or a resolver when constructing WorkflowRunner."))

    member this.ExecuteAsync<'TResponse>(request: WorkflowRequest<'TResponse>) : Task<'TResponse> =
        task {
            let key             = request.GetType().Name
            let target          = this.Resolve(key)
            let! resolvedFields = FieldValueResolver.resolveAsync request context
            let! response       = target.ExecuteAsync(request, resolvedFields, context)
            context.CaptureRaw(key, response :> obj)
            return response
        }

    member this.ExecuteRawAsync<'TResponse>(request: WorkflowRequest<'TResponse>) : Task<obj> =
        task {
            let key    = request.GetType().Name
            let target = this.Resolve(key)
            match target with
            | :? IRawTarget as raw ->
                let! resolvedFields = FieldValueResolver.resolveAsync request context
                let! result         = raw.ExecuteRawAsync(request, resolvedFields, context)
                context.CaptureRaw(key, result)
                return result
            | _ ->
                return raise (WorkflowContextException($"Target for '{key}' does not implement IRawTarget."))
        }

    member this.PollAsync<'TResponse>
            (request    : WorkflowRequest<'TResponse>,
             until      : 'TResponse -> bool,
             ?intervalMs: int,
             ?timeoutMs : int)
            : Task<'TResponse> =
        let interval = defaultArg intervalMs 500
        let timeout  = defaultArg timeoutMs 10000
        task {
            let sw   = Diagnostics.Stopwatch.StartNew()
            let mutable found  = Unchecked.defaultof<'TResponse>
            let mutable isDone = false
            while not isDone do
                let! response = this.ExecuteAsync(request)
                if until response then
                    found  <- response
                    isDone <- true
                else
                    let remaining = int64 timeout - sw.ElapsedMilliseconds
                    if remaining <= 0L then
                        raise (WorkflowContextException(
                            $"PollAsync timed out after {timeout}ms waiting for '{request.GetType().Name}'."))
                    do! Task.Delay(int (min (int64 interval) remaining))
            return found
        }

    member _.BuildAsync<'TResponse>(item: BuildableRequest<'TResponse>, accKey: Type) : Task<'TResponse> =
        task {
            let! resolved = FieldValueResolver.resolveObjectAsync (item :> BuildableRequest) context
            let json      = JsonSerializer.Serialize(resolved :> obj)
            let response  = JsonSerializer.Deserialize<'TResponse>(json, jsonOptions)
            context.Accumulate(accKey, response :> obj)
            context.CaptureRaw(item.GetType().Name, response :> obj)
            return response
        }
