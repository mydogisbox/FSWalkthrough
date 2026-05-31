module FSWalkthrough.SampleWorkflows.User

open System
open System.Net.Http
open System.Collections.Generic
open FSWalkthrough.Core
open FSWalkthrough.Http

// ── Create user ───────────────────────────────────────────────────────────────

type UserResponse = { Id: string; Email: string; FirstName: string; LastName: string; Role: string }

type CreateUserRequest =
    { Email     : IFieldValue<string>
      FirstName : IFieldValue<string>
      LastName  : IFieldValue<string>
      Role      : IFieldValue<string> }
    interface WorkflowRequest<UserResponse>
    static member Default =
        { Email     = FieldValues.generated (fun () -> $"user-{Guid.NewGuid():N}@test.com")
          FirstName = FieldValues.constant "Test"
          LastName  = FieldValues.constant "User"
          Role      = FieldValues.constant "user" }

type CreateUserStep() =
    inherit HttpStep<CreateUserRequest, UserResponse>()
    override _.Method = HttpMethod.Post
    override _.Path   = "/users"

// ── Update user address ───────────────────────────────────────────────────────

type AddressRegionResponse  = { State: string; Country: string }
type AddressInfoResponse    = { Street: string; City: string; Region: AddressRegionResponse }
type PrimaryContactResponse = { Address: AddressInfoResponse }
type ContactInfoResponse    = { Primary: PrimaryContactResponse }
type UpdateUserAddressResponse = { UserId: string; Contact: ContactInfoResponse }

type RegionFields =
    { State   : IFieldValue<string>
      Country : IFieldValue<string> }
    static member Default =
        { State   = FieldValues.constant "IL"
          Country = FieldValues.constant "US" }

type AddressFields =
    { Street : IFieldValue<string>
      City   : IFieldValue<string>
      Region : IFieldValue<RegionFields> }
    static member Default =
        { Street = FieldValues.constant "123 Main St"
          City   = FieldValues.constant "Springfield"
          Region = FieldValues.constant RegionFields.Default }

type PrimaryFields =
    { Address : IFieldValue<AddressFields> }
    static member Default =
        { Address = FieldValues.constant AddressFields.Default }

type ContactFields =
    { Primary : IFieldValue<PrimaryFields> }
    static member Default =
        { Primary = FieldValues.constant PrimaryFields.Default }

type UpdateUserAddressRequest =
    { UserId  : IFieldValue<string>
      Contact : IFieldValue<ContactFields> }
    interface WorkflowRequest<UpdateUserAddressResponse>
    static member Default =
        { UserId  = FieldValues.from (fun ctx -> ctx.Get<UserResponse>("CreateUserRequest").Id)
          Contact = FieldValues.constant ContactFields.Default }

type UpdateUserAddressStep() =
    inherit HttpStep<UpdateUserAddressRequest, UpdateUserAddressResponse>()
    override _.Method = HttpMethod.Put
    override _.Path   = "/users/{userId}/address"

// ── Get users by role ─────────────────────────────────────────────────────────

type GetUsersByRoleRequest =
    { Role : IFieldValue<string> }
    interface WorkflowRequest<UserResponse list>
    static member Default =
        { Role = FieldValues.constant "user" }

type GetUsersByRoleStep() =
    inherit HttpStep<GetUsersByRoleRequest, UserResponse list>()
    override _.Method = HttpMethod.Get
    override _.Path   = "/users"
    override _.MapQuery(fields) =
        let role = match fields.TryGetValue "Role" with | true, v -> string v | _ -> ""
        Dictionary(dict ["role", role])
