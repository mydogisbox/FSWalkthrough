module FSWalkthrough.SampleWorkflows.Order

open System.Collections.Generic
open System.Net.Http
open FSWalkthrough.Core
open FSWalkthrough.Http
open FSWalkthrough.SampleWorkflows.User

// ── Response types ────────────────────────────────────────────────────────────

type OrderItemResponse = { ProductName: string; Quantity: int; UnitPrice: decimal }
type OrderResponse     = { Id: string; UserId: string; Items: OrderItemResponse list; Status: string }

// ── AddOrderItem (buildable) ──────────────────────────────────────────────────

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

// ── CreateOrder ───────────────────────────────────────────────────────────────

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

// ── GetOrder ──────────────────────────────────────────────────────────────────

type GetOrderRequest =
    { OrderId : IFieldValue<string> }
    interface WorkflowRequest<OrderResponse>
    static member Default =
        { OrderId = FieldValues.from (fun ctx -> ctx.Get<OrderResponse>("CreateOrderRequest").Id) }

type GetOrderStep() =
    inherit HttpStep<GetOrderRequest, OrderResponse>()
    override _.Method = HttpMethod.Get
    override _.Path   = "/orders/{orderId}"

// ── Polymorphic order-line items ──────────────────────────────────────────────

// Marker interface — subtypes set AccumulationKey = typeof<OrderLineItem> to group together.
type OrderLineItem = inherit BuildableRequest

type PhysicalItem =
    { ProductName       : IFieldValue<string>
      Quantity          : IFieldValue<int>
      UnitPrice         : IFieldValue<decimal>
      ShippingAddress   : IFieldValue<string> }
    interface BuildableRequest<AddOrderItemResponse>
    static member AccumulationKey = typeof<OrderLineItem>
    static member Default =
        { ProductName     = FieldValues.constant "Item"
          Quantity        = FieldValues.constant 1
          UnitPrice       = FieldValues.constant 9.99m
          ShippingAddress = FieldValues.constant "123 Main St" }

type DigitalItem =
    { ProductName   : IFieldValue<string>
      Quantity      : IFieldValue<int>
      UnitPrice     : IFieldValue<decimal>
      DownloadUrl   : IFieldValue<string> }
    interface BuildableRequest<AddOrderItemResponse>
    static member AccumulationKey = typeof<OrderLineItem>
    static member Default =
        { ProductName = FieldValues.constant "Item"
          Quantity    = FieldValues.constant 1
          UnitPrice   = FieldValues.constant 9.99m
          DownloadUrl = FieldValues.constant "https://example.com/ebook" }

// ── Mixed-type items with distinct TResponse per subtype ─────────────────────

// Marker interface — subtypes set AccumulationKey = typeof<LineItem> to group together.
type LineItem = inherit BuildableRequest

type PhysicalLineItemResponse = { ProductName: string; Quantity: int; UnitPrice: decimal; ShippingAddress: string }
type DigitalLineItemResponse  = { ProductName: string; Quantity: int; UnitPrice: decimal; DownloadUrl: string }

type PhysicalLineItem =
    { ProductName     : IFieldValue<string>
      Quantity        : IFieldValue<int>
      UnitPrice       : IFieldValue<decimal>
      ShippingAddress : IFieldValue<string> }
    interface BuildableRequest<PhysicalLineItemResponse>
    static member AccumulationKey = typeof<LineItem>
    static member Default =
        { ProductName     = FieldValues.constant "Widget"
          Quantity        = FieldValues.constant 1
          UnitPrice       = FieldValues.constant 9.99m
          ShippingAddress = FieldValues.constant "123 Main St" }

type DigitalLineItem =
    { ProductName : IFieldValue<string>
      Quantity    : IFieldValue<int>
      UnitPrice   : IFieldValue<decimal>
      DownloadUrl : IFieldValue<string> }
    interface BuildableRequest<DigitalLineItemResponse>
    static member AccumulationKey = typeof<LineItem>
    static member Default =
        { ProductName = FieldValues.constant "E-Book"
          Quantity    = FieldValues.constant 1
          UnitPrice   = FieldValues.constant 9.99m
          DownloadUrl = FieldValues.constant "https://example.com/ebook" }

type TypeMappedOrderRequest =
    { UserId : IFieldValue<string>
      Items  : IFieldValue<ResizeArray<obj>> }
    interface WorkflowRequest<OrderResponse>
    static member Default =
        { UserId = FieldValues.from (fun ctx -> ctx.Get<UserResponse>("CreateUserRequest").Id)
          Items  = FieldValues.from (fun ctx -> ctx.GetAccumulated<LineItem>()) }

type TypeMappedOrderStep() =
    inherit HttpStep<TypeMappedOrderRequest, OrderResponse>()
    override _.Method = HttpMethod.Post
    override _.Path   = "/orders"

    override _.MapBody(fields) =
        let items =
            match fields.TryGetValue "Items" with
            | true, (:? System.Collections.IList as list) ->
                list
                |> Seq.cast<obj>
                |> Seq.map (fun item ->
                    match item with
                    | :? PhysicalLineItemResponse as p ->
                        dict ["productName", box p.ProductName; "quantity", box p.Quantity;
                              "unitPrice", box p.UnitPrice; "shippingAddress", box p.ShippingAddress]
                        :> obj
                    | :? DigitalLineItemResponse as d ->
                        dict ["productName", box d.ProductName; "quantity", box d.Quantity;
                              "unitPrice", box d.UnitPrice; "downloadUrl", box d.DownloadUrl]
                        :> obj
                    | other -> other)
                |> ResizeArray<obj>
                :> obj
            | _ -> box (ResizeArray<obj>())
        Dictionary(dict ["UserId", fields["UserId"]; "Items", items])
