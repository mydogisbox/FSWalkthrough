module FSWalkthrough.SampleApi.App

open System
open System.IdentityModel.Tokens.Jwt
open System.Security.Claims
open System.Text
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.DependencyInjection
open Microsoft.IdentityModel.Tokens
open FSWalkthrough.SampleApi.Models
open FSWalkthrough.SampleApi.Service

let [<Literal>] private JwtKey    = "sample-api-secret-key-for-testing-only-must-be-at-least-32-chars"
let [<Literal>] private JwtIssuer = "FSWalkthrough.SampleApi"

let private signingKey = SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey))

let private issueToken (userId: string) =
    let claims = [| Claim(ClaimTypes.NameIdentifier, userId) |]
    let creds  = SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
    let token  = JwtSecurityToken(
                    issuer            = JwtIssuer,
                    claims            = claims,
                    expires           = DateTime.UtcNow.AddHours(1.0),
                    signingCredentials = creds)
    JwtSecurityTokenHandler().WriteToken(token)

[<EntryPoint>]
let main args =
    let builder = WebApplication.CreateBuilder(args)

    builder.Services.AddSingleton<SampleApiService>() |> ignore

    builder.Services
        .AddAuthentication("Bearer")
        .AddJwtBearer("Bearer", fun opts ->
            opts.TokenValidationParameters <-
                TokenValidationParameters(
                    ValidateIssuer           = true,
                    ValidIssuer              = JwtIssuer,
                    ValidateAudience         = false,
                    ValidateLifetime         = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey         = signingKey)) |> ignore

    builder.Services.AddAuthorization(fun opts -> opts.FallbackPolicy <- null) |> ignore

    let app = builder.Build()
    app.Urls.Add("http://localhost:4200")
    app.UseAuthentication() |> ignore
    app.UseAuthorization()  |> ignore

    app.MapPost("/auth/login",
        Func<LoginRequest, IResult>(fun req ->
            if String.IsNullOrWhiteSpace(req.Username) || String.IsNullOrWhiteSpace(req.Password) then
                Results.Unauthorized()
            else
                let userId = Guid.NewGuid().ToString()
                Results.Ok({ Token = issueToken userId; UserId = userId })
        )).AllowAnonymous() |> ignore

    app.MapPost("/users",
        Func<SampleApiService, CreateUserRequest, IResult>(fun svc req ->
            let user = svc.CreateUser(req)
            Results.Created($"/users/{user.Id}", user)
        )).RequireAuthorization() |> ignore

    app.MapGet("/users",
        Func<SampleApiService, string, IResult>(fun svc role ->
            let r = if String.IsNullOrEmpty(role) then None else Some role
            Results.Ok(svc.GetUsers(r))
        )).RequireAuthorization() |> ignore

    app.MapGet("/users/{id}",
        Func<SampleApiService, string, IResult>(fun svc id ->
            try  Results.Ok(svc.GetUser(id))
            with :? System.Collections.Generic.KeyNotFoundException ->
                Results.NotFound({| error = $"User '{id}' not found." |})
        )).RequireAuthorization() |> ignore

    app.MapPost("/orders",
        Func<SampleApiService, CreateOrderRequest, IResult>(fun svc req ->
            try
                let order = svc.CreateOrder(req)
                Results.Created($"/orders/{order.Id}", order)
            with :? ArgumentException as ex ->
                Results.BadRequest({| error = ex.Message |})
        )).RequireAuthorization() |> ignore

    app.MapGet("/orders/{id}",
        Func<SampleApiService, string, IResult>(fun svc id ->
            try  Results.Ok(svc.GetOrder(id))
            with :? System.Collections.Generic.KeyNotFoundException ->
                Results.NotFound({| error = $"Order '{id}' not found." |})
        )).RequireAuthorization() |> ignore

    app.MapPut("/users/{userId}/address",
        Func<SampleApiService, string, UpdateUserAddressRequest, IResult>(fun svc userId req ->
            try  Results.Ok(svc.UpdateUserAddress(userId, req))
            with :? System.Collections.Generic.KeyNotFoundException ->
                Results.NotFound({| error = $"User '{userId}' not found." |})
        )).RequireAuthorization() |> ignore

    app.MapPost("/users/{userId}/tags/{tag}",
        Func<SampleApiService, string, string, IResult>(fun svc userId tag ->
            try
                svc.TagUser(userId, tag)
                Results.Created($"/users/{userId}/tags/{tag}", null)
            with :? System.Collections.Generic.KeyNotFoundException ->
                Results.NotFound({| error = $"User '{userId}' not found." |})
        )).RequireAuthorization() |> ignore

    app.MapGet("/echo/headers",
        Func<HttpContext, IResult>(fun ctx ->
            let headers =
                ctx.Request.Headers
                |> Seq.map (fun kv -> kv.Key.ToLower(), kv.Value.ToString())
                |> dict
            Results.Ok(headers)
        )).AllowAnonymous() |> ignore

    app.MapGet("/health", Func<IResult>(fun () -> Results.Ok())) |> ignore

    app.Run()
    0
