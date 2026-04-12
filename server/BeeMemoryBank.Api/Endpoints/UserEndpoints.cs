using BeeMemoryBank.Api.Middleware;
using BeeMemoryBank.Api.Models;
using BeeMemoryBank.Core.Interfaces;
using BeeMemoryBank.Core.Services;

namespace BeeMemoryBank.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users");

        group.MapGet("/", async (IUserRepository repo, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var users = await repo.ListActiveAsync();
            return Results.Ok(users.Select(u => new UserListItemResponse(
                u.Id, u.Username, u.DisplayName, u.Role, u.CreatedAt, u.LastLoginAt)));
        });

        group.MapPost("/", async (CreateUserRequest req, IUserRepository userRepo, UserService userService, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Only superadmin can create users"), statusCode: 403);

            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Only superadmin can create users"), statusCode: 403);

            try
            {
                var user = await userService.CreateUserAsync(req.Username, req.DisplayName, req.Password, req.Role);
                return Results.Ok(new UserListItemResponse(user.Id, user.Username, user.DisplayName, user.Role, user.CreatedAt, user.LastLoginAt));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });

        group.MapPut("/{id:int}", async (int id, UpdateUserRequest req, IUserRepository userRepo, UserService userService, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Only superadmin can update users"), statusCode: 403);

            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Only superadmin can update users"), statusCode: 403);

            try
            {
                await userService.UpdateUserAsync(id, req.DisplayName, req.Role, req.Password);
                return Results.Ok();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
        });

        group.MapDelete("/{id:int}", async (int id, UserService userService, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Only superadmin can delete users"), statusCode: 403);

            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Only superadmin can delete users"), statusCode: 403);

            try
            {
                await userService.DeleteUserAsync(id);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
        });

        // Self-service password change — any authenticated user can change their own password
        group.MapPost("/me/change-password", async (ChangePasswordRequest req, UserService userService, HttpContext ctx) =>
        {
            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Unauthorized"), statusCode: 403);

            var userIdStr = ctx.Request.Headers["X-User-Id"].FirstOrDefault();
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId))
                return Results.Json(new ErrorResponse("User not identified"), statusCode: 401);

            try
            {
                await userService.ChangePasswordAsync(userId, req.OldPassword, req.NewPassword);
                return Results.Ok();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Json(new ErrorResponse("Incorrect current password"), statusCode: 403);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 400);
            }
        });

        group.MapPost("/{id:int}/change-password", async (int id, ChangeUserPasswordRequest req, UserService userService, HttpContext ctx) =>
        {
            var role = ctx.Request.Headers["X-User-Role"].FirstOrDefault();
            if (role != "superadmin")
                return Results.Json(new ErrorResponse("Only superadmin can change passwords"), statusCode: 403);

            if (!InternalKeyValidator.Validate(ctx))
                return Results.Json(new ErrorResponse("Only superadmin can change passwords"), statusCode: 403);

            try
            {
                await userService.AdminChangePasswordAsync(id, req.NewPassword);
                return Results.Ok();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse("User not found"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new ErrorResponse(ex.Message), statusCode: 409);
            }
        });
    }
}
