using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using WorkflowEngine.Api.Endpoints;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.DependencyInjection;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.DependencyInjection;
using WorkflowEngine.Service.Services;

var builder = WebApplication.CreateBuilder(args);

var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "workflow-engine-dev";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "workflow-engine-api";
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key is not configured.");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = signingKey
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

// Read-only workflow context sources (sys.* / config.*) and a clock for sys.now/today.
var workflowContextOptions = builder.Configuration
    .GetSection(WorkflowContextOptions.SectionName)
    .Get<WorkflowContextOptions>() ?? new WorkflowContextOptions();
builder.Services.AddSingleton(workflowContextOptions);
builder.Services.AddSingleton(TimeProvider.System);

// scriptTask JavaScript execution limits (Jint sandbox: timeout / statements / memory).
var scriptOptions = builder.Configuration
    .GetSection(ScriptOptions.SectionName)
    .Get<ScriptOptions>() ?? new ScriptOptions();
builder.Services.AddSingleton(scriptOptions);

builder.Services
    .AddServiceLayer()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (WorkflowDomainException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (WorkflowUnauthorizedException ex)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Workflow Engine API v1");
    });

    using var scope = app.Services.CreateScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    await initializer.InitializeDatabaseAsync();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapWorkflowDefinitionEndpoints();
app.MapWorkflowInstanceEndpoints();

app.Run();
