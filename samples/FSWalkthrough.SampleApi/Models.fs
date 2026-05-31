module FSWalkthrough.SampleApi.Models

open System.Collections.Generic

type LoginRequest  = { Username: string; Password: string }
type LoginResponse = { Token: string; UserId: string }

type CreateUserRequest = { Email: string; FirstName: string; LastName: string; Role: string }
type UserResponse      = { Id: string; Email: string; FirstName: string; LastName: string; Role: string }

type OrderItemRequest  = { ProductName: string; Quantity: int; UnitPrice: decimal }
type OrderItemResponse = { ProductName: string; Quantity: int; UnitPrice: decimal }
type CreateOrderRequest  = { UserId: string; Items: List<OrderItemRequest> }
type OrderResponse       = { Id: string; UserId: string; Items: List<OrderItemResponse>; Status: string }

type RegionInfo    = { State: string; Country: string }
type AddressInfo   = { Street: string; City: string; Region: RegionInfo }
type PrimaryContact = { Address: AddressInfo }
type ContactInfo   = { Primary: PrimaryContact }
type UpdateUserAddressRequest  = { Contact: ContactInfo }
type UpdateUserAddressResponse = { UserId: string; Contact: ContactInfo }
