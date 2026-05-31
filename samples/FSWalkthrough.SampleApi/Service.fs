module FSWalkthrough.SampleApi.Service

open System
open System.Collections.Concurrent
open System.Collections.Generic
open FSWalkthrough.SampleApi.Models

type SampleApiService() =
    let users  = ConcurrentDictionary<string, UserResponse>()
    let orders = ConcurrentDictionary<string, OrderResponse>()

    member _.CreateUser(req: CreateUserRequest) : UserResponse =
        let user =
            { Id        = Guid.NewGuid().ToString()
              Email     = req.Email
              FirstName = req.FirstName
              LastName  = req.LastName
              Role      = req.Role }
        users[user.Id] <- user
        user

    member _.GetUser(id: string) : UserResponse =
        match users.TryGetValue(id) with
        | true, user -> user
        | _ -> raise (KeyNotFoundException($"User '{id}' not found."))

    member _.GetUsers(role: string option) : UserResponse list =
        users.Values
        |> Seq.filter (fun u ->
            role |> Option.forall (fun r -> u.Role.Equals(r, StringComparison.OrdinalIgnoreCase)))
        |> Seq.toList

    member _.CreateOrder(req: CreateOrderRequest) : OrderResponse =
        if not (users.ContainsKey(req.UserId)) then
            invalidArg "req" $"User '{req.UserId}' does not exist."
        let order =
            { Id     = Guid.NewGuid().ToString()
              UserId = req.UserId
              Items  = req.Items |> Seq.map (fun i ->
                           { ProductName = i.ProductName; Quantity = i.Quantity; UnitPrice = i.UnitPrice })
                       |> List<OrderItemResponse>
              Status = "pending" }
        orders[order.Id] <- order
        order

    member _.GetOrder(id: string) : OrderResponse =
        match orders.TryGetValue(id) with
        | true, order -> order
        | _ -> raise (KeyNotFoundException($"Order '{id}' not found."))

    member _.UpdateUserAddress(userId: string, req: UpdateUserAddressRequest) : UpdateUserAddressResponse =
        if not (users.ContainsKey(userId)) then
            raise (KeyNotFoundException($"User '{userId}' not found."))
        { UserId = userId; Contact = req.Contact }
