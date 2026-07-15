using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Serilog;
using WorkflowEngine.Api.Endpoints;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.DependencyInjection;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Service.DependencyInjection;
using WorkflowEngine.Service.Services;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting API host...");
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

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

    builder.Services.AddOpenApi(options =>
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Workflow Engine API",
                Version = "1.0.0",
                Description = """
                    Runtime engine for BPMN-aligned business process workflows.

                    Workflow definitions are immutable, versioned JSON snapshots created in the
                    single-file visual editor (`workflow-editor.html`). Definitions are loaded,
                    validated, published, and then instantiated. Instances progress through flow
                    nodes (startEvent, userTask, task, serviceTask, scriptTask, exclusiveGateway,
                    endEvent, errorEndEvent, errorBoundaryEvent, intermediateMessageCatchEvent,
                    messageStartEvent) connected by sequence flows, with pass-through routing,
                    role enforcement, claim locking, and NCalc condition evaluation.

                    Start events may derive a generic domain business key from a required
                    string start variable. Message-start output mappings are both payload
                    mappings and typed start-variable declarations; their business key must
                    reference a required scalar string mapping with an explicit payload path.
                    Keys are exact and case-sensitive after trimming, scoped across every
                    version of a workflow key, and use an atomic PostgreSQL claim with either
                    active-only or permanent uniqueness.

                    Service-response and intermediate-message-catch output mappings are also
                    typed contracts. External JSON values are strict, missing paths may use
                    ordered defaults, and mapping/process NCalc validations run against the
                    final overlay before the atomic output batch is persisted.

                    ## Authentication
                    The `/api/instances` and `/api/workflows` groups require a bearer JWT
                    validated against a shared symmetric key (`Jwt:Key`). The Blazor UI mints its
                    own dev token from `/token`; for production swap `AddJwtBearer` to a real OIDC
                    identity provider. The two message endpoints
                    (`POST /api/instances/{id}/message` and `POST /api/workflows/{workflowKey}/message-start`)
                    are `AllowAnonymous` - they authenticate the caller against the node's
                    configured client id/secret + required custom header, not the user JWT.

                    ## Status codes
                    - 200 OK, 201 Created, 204 No Content for success
                    - 400 Bad Request for domain errors (WorkflowDomainException) - unpublished
                    workflow, missing variable, unavailable/forbidden flow, role/claim mismatch,
                    gateway with no match, service/script task failure with no error boundary
                    - 401 Unauthorized for a client id/secret mismatch on the message endpoints
                    - 403 Forbidden for a missing required role on the workflow-definition endpoints
                    - 404 Not Found when a referenced instance or definition id does not exist
                    - 409 Conflict when a task or multi-instance execution was concurrently completed
                    """,
            };

            document.Tags = new HashSet<OpenApiTag>
            {
                new()
                {
                    Name = "Workflow Definitions",
                    Description = "Create, version, publish, and delete workflow definitions (JSON snapshots from the visual editor)."
                },
                new()
                {
                    Name = "Workflow Instances",
                    Description = "Start, list, inspect, claim, advance, cancel, and message workflow instances."
                },
                new()
                {
                    Name = "Multi-Instance Executions",
                    Description = "Discover and take authorized parent-level interrupt actions for active multi-instance user tasks."
                }
            };

            document.Components ??= new OpenApiComponents();
            var securityScheme = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT bearer token validated against the configured `Jwt:Key`."
            };
            document.Components.SecuritySchemes = document.Components.SecuritySchemes ?? new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = securityScheme;

            return Task.CompletedTask;
        });

        // Attach Bearer security only to operations that are NOT AllowAnonymous, and
        // fill in the description Swagger can't infer for the renamed `var` query param.
        options.AddOperationTransformer((operation, context, _) =>
        {
            // The AllowAnonymous metadata is exposed via the descriptor's endpoint
            // metadata collection; when absent the operation requires the group's
            // JWT authorization, so attach the Bearer security requirement.
            var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;
            var isAnonymous = endpointMetadata is not null
                && endpointMetadata.Any(m => m is IAllowAnonymous || (m?.GetType().Name == "AllowAnonymousAttribute"));

            if (!isAnonymous)
            {
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
                });
            }

            if (operation.Parameters is not null)
            {
                foreach (var param in operation.Parameters)
                {
                    if (string.Equals(param.Name, "var", StringComparison.Ordinal) && param.In == ParameterLocation.Query)
                    {
                        param.Description = "Repeated `var=name:value` filter. Exact, case-insensitive match on an instance variable's scalar value. Multiple entries are AND-combined. Array/object variables never match.";
                    }
                }
            }

            return Task.CompletedTask;
        });
    });

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
            Log.Warning(ex, "Workflow domain exception: {Message}", ex.Message);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (BusinessKeyConflictException ex)
        {
            Log.Warning("Workflow business-key conflict with existing instance {ExistingInstanceId}.", ex.ExistingInstanceId);
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.Headers.Location = $"/api/instances/{ex.ExistingInstanceId}";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "business_key_conflict",
                error = ex.Message,
                existingInstanceId = ex.ExistingInstanceId
            });
        }
        catch (WorkflowConflictException ex)
        {
            Log.Warning(ex, "Workflow conflict: {Message}", ex.Message);
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (WorkflowUnauthorizedException ex)
        {
            Log.Warning(ex, "Workflow unauthorized exception: {Message}", ex.Message);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    });

    app.UseSerilogRequestLogging();

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
    app.MapUserTaskEndpoints();
    app.MapMultiInstanceExecutionEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public partial class Program;
