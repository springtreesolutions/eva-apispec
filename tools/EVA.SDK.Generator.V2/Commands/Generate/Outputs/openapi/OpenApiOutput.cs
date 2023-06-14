﻿using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using EVA.API.Spec;
using EVA.SDK.Generator.V2.Exceptions;
using EVA.SDK.Generator.V2.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Validations;
using Microsoft.OpenApi.Writers;

namespace EVA.SDK.Generator.V2.Commands.Generate.Outputs.openapi;

internal partial class OpenApiOutput : IOutput<OpenApiOptions>
{
  private class State
  {
    internal readonly Dictionary<string, bool> SupportsBackendIDCache = new();
  }

  public string? OutputPattern => null;

  public string[] ForcedRemoves => new[] { "generics", "unused-type-params", "errors", "event-exports", "inheritance" };

  private const string Parameter_Header_UserAgent = "p1";
  private const string Parameter_Header_IdsMode = "p2";
  private const string Parameter_Header_AsyncCallback = "p3";

  private const string Schema_Error = "eva_error_400";
  private const string Example_400_RequestValidation = "eva_example_400_RequestValidationFailure";
  private const string Example_403_Forbidden = "eva_example_403_Forbidden";

  public async Task Write(OutputContext<OpenApiOptions> ctx)
  {
    var model = GetModel(ctx.Input, ctx.Options.Host);

    foreach (var error in model.Validate(ValidationRuleSet.GetDefaultRuleSet()))
    {
      ctx.Logger.LogWarning("Validation error: {Error}", error.ToString());
    }

    var version = ctx.Options.Version switch
    {
      "v2" => OpenApiSpecVersion.OpenApi2_0,
      _ => OpenApiSpecVersion.OpenApi3_0
    };

    var filename = ctx.Options.Format == "yaml" ? "openapi.yaml" : "openapi.json";
    await using var file = ctx.Writer.WriteStreamAsync(filename);
    await using var textWriter = new StreamWriter(file.Value);

    IOpenApiWriter openApiWriter = ctx.Options.Format == "yaml"
      ? new OpenApiYamlWriter(textWriter, new OpenApiWriterSettings())
      : new OpenApiJsonWriter(textWriter, new OpenApiJsonWriterSettings { Terse = ctx.Options.Terse });

    model.Serialize(openApiWriter, version);
  }

  internal static string Cleanup(ApiDefinitionModel input)
  {
    string? result = null;
    // Remove the Error field from all response messages
    foreach (var service in input.Services)
    {
      var responseType = input.Types[service.ResponseTypeID];
      if (responseType.Properties.TryGetValue("Error", out var v))
      {
        result = v.Type.Name;
        responseType.Properties = responseType.Properties.Remove("Error");
      }
    }

    return result;
  }

  internal OpenApiDocument GetModel(ApiDefinitionModel input, string host)
  {
    var state = new State();
    var errorObjectID = Cleanup(input);

    var server = string.IsNullOrWhiteSpace(host)
      ? new OpenApiServer
      {
        Url = "https://api.{region}.{customer}.{environment}.eva-online.cloud/",
        Variables = new Dictionary<string, OpenApiServerVariable>
        {
          { "region", new OpenApiServerVariable { Default = "euw" } },
          { "customer", new OpenApiServerVariable { Default = "acme" } },
          { "environment", new OpenApiServerVariable { Default = "test" } },
        }
      }
      : new OpenApiServer { Url = host };

    // Base
    var model = new OpenApiDocument
    {
      Info = new OpenApiInfo
      {
        Version = "1.0.0",
        Description = "OpenApi description of EVA",
        Title = "EVA",
        Contact = new OpenApiContact
        {
          Email = "ruben.oost@newblack.io",
          Name = "Ruben Oost",
          Url = new Uri("https://newblack.io/")
        }
      },
      Servers = new List<OpenApiServer> { server },
      Paths = new OpenApiPaths(),
      Components = new OpenApiComponents(),
      Tags = input.Services.Select(s => TagFromAssembly(s.Assembly)).Concat(new[]{"DataLake"}).Distinct().Order().Select(s => new OpenApiTag { Name = s, Description = s }).ToList(),
      SecurityRequirements = new List<OpenApiSecurityRequirement>
      {
        new()
        {
          { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "eva-auth" } }, new List<string>() }
        }
      }
    };

    model.Components.SecuritySchemes.Add("eva-auth", new OpenApiSecurityScheme
    {
      Description = "The default authentication mechanism when communicating with EVA",
      Type = SecuritySchemeType.ApiKey,
      Name = "Authorization",
      In = ParameterLocation.Header
    });
    model.Components.SecuritySchemes.Add("eva-auth-elevated", new OpenApiSecurityScheme
    {
      Name = "EVA-Elevation-Token",
      In = ParameterLocation.Header,
      Type = SecuritySchemeType.ApiKey,
      Description = "Authenticate using an elevated token. This allows temporary access to resources that are otherwise not accessible."
    });
    model.Components.SecuritySchemes.Add("eva-auth-apptoken", new OpenApiSecurityScheme
    {
      Name = "EVA-App-Token",
      In = ParameterLocation.Header,
      Type = SecuritySchemeType.ApiKey,
      Description = "Authenticate using an application token. This allows building an order as a non-logged in user."
    });

    // Parameters
    model.Components.Parameters.Add(Parameter_Header_UserAgent, new()
    {
      In = ParameterLocation.Header,
      Name = "EVA-User-Agent",
      Description = "The user agent that is making these calls. Don't make this specific per device/browser but per application. This should be of the form: `MyFirstUserAgent/1.0.0`",
      Required = true,
      AllowEmptyValue = false,
      Schema = new OpenApiSchema
      {
        Default = new OpenApiString("eva-sdk-openapi"),
        Type = "string"
      },
      Style = ParameterStyle.Simple
    });
    model.Components.Parameters.Add(Parameter_Header_IdsMode, new()
    {
      In = ParameterLocation.Header,
      Description = "The IDs mode to run this request in. Currently only `ExternalIDs` is supported.",
      Name = "EVA-IDs-Mode",
      Required = false,
      AllowEmptyValue = false,
      Schema = new OpenApiSchema
      {
        Type = "string",
        Enum = new List<IOpenApiAny>
        {
          new OpenApiString("ExternalIDs")
        }
      },
      Style = ParameterStyle.Simple
    });

    model.Components.Parameters.Add(Parameter_Header_AsyncCallback, new()
    {
      In = ParameterLocation.Header,
      Name = "EVA-Async-Callback",
      Description = "Indicate how the caller should be notified when the asynchronous operation is complete. This is a serialized JSON object. Currently we only support the `email` property. Use `me` as a value to be notified on the emailaddress of the current user.",
      Required = false,
      AllowEmptyValue = false,
      Schema = new OpenApiSchema
      {
        Type = "string",
        Default = new OpenApiString("{}")
      },
      Style = ParameterStyle.Simple
    });

    // Render each datalake endpoint
    foreach (var dl in input.DatalakeExports)
    {
      model.Paths["/datalake/" + dl.Name] = ToDatalakeItem(input, dl);
    }

    // Render each service
    foreach (var service in input.Services)
    {
      var pathItem = ToPathItem(state, input, service);
      model.Paths[service.Path] = pathItem;
    }

    // Render each type
    foreach (var (id, type) in input.Types)
    {
      model.Components.Schemas.Add(FixName(id), ToSchema(input, type));
    }

    // 400 error
    model.Components.Schemas.Add(Schema_Error, new OpenApiSchema
    {
      Type = "object",
      Required = new HashSet<string> { "Error" },
      Properties = new Dictionary<string, OpenApiSchema> { { "Error", ToSchema(errorObjectID) } }
    });

    model.Components.Examples.Add(Example_400_RequestValidation, new OpenApiExample
    {
      Summary = "An example BadRequest response",
      Value = new OpenApiObject
      {
        {
          "Error", new OpenApiObject
          {
            { "Message", new OpenApiString("Validation of the request message failed: Field ABC has an invalid value for a Product") },
            { "Type", new OpenApiString("RequestValidationFailure") },
            { "Code", new OpenApiString("COVFEFE") },
            { "RequestID", new OpenApiString("576b62dd71894e3281a4d84951f44e70") }
          }
        }
      }
    });

    model.Components.Examples.Add(Example_403_Forbidden, new OpenApiExample
    {
      Summary = "An example Forbidden response",
      Value = new OpenApiObject
      {
        {
          "Error", new OpenApiObject
          {
            { "Message", new OpenApiString("You are not authorized to execute this request.") },
            { "Type", new OpenApiString("Forbidden") },
            { "Code", new OpenApiString("COVFEFE") },
            { "RequestID", new OpenApiString("576b62dd71894e3281a4d84951f44e70") }
          }
        }
      }
    });

    return model;
  }

  private static OpenApiSchema ToSchema(ApiDefinitionModel input, TypeSpecification type)
  {
    if (type.EnumIsFlag.HasValue)
    {
      var totals = type.EnumValues.ToTotals();
      var possibleValues = string.Join('\n', totals.OrderBy(kv => kv.Value).Select(kv => $"* `{kv.Value}` - {kv.Key}"));

      if (type.EnumIsFlag is true)
      {
        return new OpenApiSchema
        {
          Type = "integer",
          Description = $"Flags enum, combine any of the below values:\n\n{possibleValues}"
        };
      }

      if (type.EnumIsFlag is false)
      {
        return new OpenApiSchema
        {
          Type = "integer",
          Enum = totals.Select(kv => new OpenApiInteger((int)kv.Value) as IOpenApiAny).ToList(),
          Description = $"Possible values:\n\n{possibleValues}"
        };
      }
    }

    var result = new OpenApiSchema
    {
      Type = "object",
      AdditionalPropertiesAllowed = false,
      Properties = new Dictionary<string, OpenApiSchema>()
    };

    foreach (var (name, prop) in type.Properties)
    {
      string? dmBackendId = null;
      if (prop.DataModelInformation is { SupportsBackendID: true } dmi)
      {
        dmBackendId = dmi.Name;
      }

      var schema = ToSchema(input, prop.Type, dmBackendId);
      schema.Description = prop.Description ?? string.Empty;

      if (prop.DataModelInformation is { Name: var dmn })
      {
        schema.Description += $"\n\nThis is the ID of a `{dmn}`";
      }

      if (prop.StringLengthConstraint is { } slc)
      {
        schema.MinLength = slc.Min;
        schema.MaxLength = slc.Max;
        schema.Description += $"\n\nThis string must be between {slc.Min} (incl) and {slc.Max} (incl) characters long.";
      }

      if (prop.AllowedValues is { Length: > 0 } allowedValues)
      {
        schema.Enum = allowedValues.Select<string, IOpenApiAny>(v => new OpenApiString(v)).ToList();
      }

      if (prop.Required is { Effective: not null } required)
      {
        schema.Description += $"\n\n**Required since {required.Introduced}:** {required.Comment}\n\n**Will be enforced in {required.Effective}**";
      }

      if (prop.Deprecated is { } deprecated)
      {
        schema.Deprecated = true;
        schema.Description += $"\n\n**Deprecated since {deprecated.Introduced}:** {deprecated.Comment}\n\n**Will be removed in {deprecated.Effective}**";
      }

      result.Properties.Add(name, schema);
    }

    result.Required = type.Properties.Where(p => !p.Value.Type.Nullable).Select(p => p.Key).OrderBy(x => x).ToImmutableHashSet();

    if (type.Extends != null)
    {
      result.AllOf = new List<OpenApiSchema> { ToSchema(input, type.Extends) };
    }

    return result;
  }

  private static OpenApiSchema ToSchema(ApiDefinitionModel input, TypeReference type, string? dmBackendID = null)
  {
    var s = ToSchema_IgnoreNull(input, type, dmBackendID);
    if (type.Nullable) s.Nullable = true;
    return s;
  }

  private static OpenApiSchema ToSchema_IgnoreNull(ApiDefinitionModel input, TypeReference type, string? dmBackendID)
  {
    if (type.Name is ApiSpecConsts.Int16 or ApiSpecConsts.Int32 or ApiSpecConsts.Int64 && dmBackendID != null)
    {
      var s = ToSchema_IgnoreNull(input, type, null);
      s.Title = $"{dmBackendID} ID";
      return new OpenApiSchema
      {
        OneOf = new List<OpenApiSchema>
        {
          s,
          new() { Type = "string", Title = $"{dmBackendID} BackendID", Description = "Make sure to set the `EVA-IDs-Mode` header to `ExternalIDs` when using this" },
        }
      };
    }

    if (type.Name == ApiSpecConsts.Int16) return new OpenApiSchema { Type = "integer" };
    if (type.Name == ApiSpecConsts.Int32) return new OpenApiSchema { Type = "integer", Format = "int32" };
    if (type.Name == ApiSpecConsts.Int64) return new OpenApiSchema { Type = "integer", Format = "int64" };
    if (type.Name == ApiSpecConsts.String) return new OpenApiSchema { Type = "string" };
    if (type.Name == ApiSpecConsts.Binary) return new OpenApiSchema { Type = "string", Format = "byte" };
    if (type.Name == ApiSpecConsts.Bool) return new OpenApiSchema { Type = "boolean" };
    if (type.Name == ApiSpecConsts.Guid) return new OpenApiSchema { Type = "string", Format = "uuid" };
    if (type.Name == ApiSpecConsts.Float32) return new OpenApiSchema { Type = "number", Format = "float" };
    if (type.Name == ApiSpecConsts.Float64) return new OpenApiSchema { Type = "number", Format = "double" };
    if (type.Name == ApiSpecConsts.Float128) return new OpenApiSchema { Type = "number", Format = "double" };
    if (type.Name == ApiSpecConsts.Duration) return new OpenApiSchema { Type = "string", Format = "duration" };
    if (type.Name == ApiSpecConsts.Object) return new OpenApiSchema { Type = "object", AdditionalPropertiesAllowed = true };
    if (type.Name == ApiSpecConsts.Any) return new OpenApiSchema { Type = "object", AdditionalPropertiesAllowed = true };
    if (type.Name == ApiSpecConsts.Date) return new OpenApiSchema { Type = "string", Format = "date-time" };
    if (type.Name == ApiSpecConsts.Specials.Array) return new OpenApiSchema { Type = "array", Items = ToSchema(input, type.Arguments.Single(), dmBackendID) };

    if (type.Name == ApiSpecConsts.Specials.Option)
    {
      return new OpenApiSchema
      {
        AnyOf = type.Arguments
          .Select(a => ToSchema(input, a))
          .ToList()
      };
    }

    if (type.Name == ApiSpecConsts.Specials.Map)
    {
      var keyType = type.Arguments[0].Name;
      if (keyType is ApiSpecConsts.String or ApiSpecConsts.Int64 or ApiSpecConsts.Float128 or ApiSpecConsts.Date || char.IsUpper(keyType[0]) && input.Types[keyType].EnumIsFlag.HasValue)
      {
        return new OpenApiSchema
        {
          Type = "object",
          AdditionalPropertiesAllowed = true,
          AdditionalProperties = ToSchema(input, type.Arguments[1])
        };
      }
    }

    if (!type.Name.StartsWith("_") && !type.Arguments.Any())
    {
      return ToSchema(type.Name);
    }

    throw new SdkException($"Cannot build openapi schema from {type.Name}");
  }

  private static OpenApiSchema ToSchema(string id)
  {
    return new OpenApiSchema
    {
      Reference = new OpenApiReference
      {
        Id = FixName(id),
        Type = ReferenceType.Schema
      }
    };
  }

  private static OpenApiPathItem ToDatalakeItem(ApiDefinitionModel input, DatalakeExportTarget dl)
  {
    var type = input.Types[dl.DataType];

    return new OpenApiPathItem
    {
      Summary = dl.Name,
      Description = $"Not a real service, but the response shows the format of the {dl.Name} export.",
      Parameters = new List<OpenApiParameter>(),
      Operations = new Dictionary<OperationType, OpenApiOperation>
      {
        {
          OperationType.Get, new OpenApiOperation
          {
            Summary = $"{dl.Name} Export",
            Description = $"Not a real service, but the response shows the format of the {dl.Name} export.",
            OperationId = $"datalake_{dl.Name}",
            Tags = new List<OpenApiTag> { new() { Name = TagFromAssembly(type.Assembly), Description = TagFromAssembly(type.Assembly) } },
            Security = new List<OpenApiSecurityRequirement>(),
            Responses = new OpenApiResponses
            {
              {
                "200", new OpenApiResponse
                {
                  Description = $"The format of the {dl.Name} export.",
                  Content = new Dictionary<string, OpenApiMediaType>
                  {
                    {
                      "application/json", new OpenApiMediaType
                      {
                        Schema = ToSchema(dl.DataType),
                      }
                    }
                  }
                }
              }
            }
          }
        }
      }
    };
  }

  private static OpenApiPathItem ToPathItem(State state, ApiDefinitionModel input, ServiceModel service)
  {
    string MapUserTypes(ApiSpecUserTypes t)
    {
      var values = Enum.GetValues<ApiSpecUserTypes>();
      values = values
        .Where(v => v != ApiSpecUserTypes.None && t.HasFlag(v))
        .ToArray();

      if (values.Length == 0) return string.Empty;

      var str = $"`{values[0]}`";
      for (var i = 1; i < values.Length; i++)
      {
        str += $" or `{values[i]}`";
      }

      return str;
    }

    var description = input.Types[service.RequestTypeID].Description ?? $"The {service.Name} service";

    var requiresAuthentication = !service.AuthInformation.RequiredPermissions.All(p => p is { Functionality: null, Scope: null, UserTypes: null });
    var supportsExternalID = SupportsExternalIdsMode(state, input, service.RequestTypeID);

    if (requiresAuthentication)
    {
      description += "\n\n---";

      foreach (var x in service.AuthInformation.RequiredPermissions)
      {
        description += "\n**Authentication:**\n\n";
        description += x switch
        {
          { Functionality: null, Scope: null, UserTypes: > 0 } => $"Requires a user of type {MapUserTypes(x.UserTypes.Value)}",
          { Functionality: not null, Scope: null, UserTypes: > 0 } => $"Requires a user of type {MapUserTypes(x.UserTypes.Value)} with functionality `{x.Functionality}`",
          { Functionality: not null, Scope: not null, UserTypes: > 0 } => $"Requires a user of type {MapUserTypes(x.UserTypes.Value)} with functionality `{x.Functionality}:{x.Scope}`",
          { Functionality: not null, Scope: null } => $"Requires any user with functionality `{x.Functionality}`",
          { Functionality: not null, Scope: not null } => $"Requires any user with functionality `{x.Functionality}:{x.Scope}`",
          { Functionality: null, Scope: null } => "This service does not require authentication",
          _ => "> ???"
        };
      }
    }

    var parameters = new List<OpenApiParameter>
    {
      new()
      {
        Reference = new OpenApiReference
        {
          Type = ReferenceType.Parameter,
          Id = Parameter_Header_UserAgent
        }
      }
    };

    if (supportsExternalID)
    {
      parameters.Add(new()
      {
        Reference = new OpenApiReference
        {
          Type = ReferenceType.Parameter,
          Id = Parameter_Header_IdsMode
        }
      });
    }

    if (service.Name.EndsWith("_Async"))
    {
      parameters.Add(new()
      {
        Reference = new OpenApiReference
        {
          Type = ReferenceType.Parameter,
          Id = Parameter_Header_AsyncCallback
        }
      });
    }

    return new OpenApiPathItem
    {
      Summary = service.Name,
      Description = description,
      Parameters = parameters,
      Operations = new Dictionary<OperationType, OpenApiOperation>
      {
        {
          OperationType.Post, new OpenApiOperation
          {
            Summary = service.Name,
            Description = description,
            OperationId = service.Name,
            Tags = new List<OpenApiTag> { new() { Name = TagFromAssembly(service.Assembly), Description = TagFromAssembly(service.Assembly) } },
            Security = !requiresAuthentication
              ? new List<OpenApiSecurityRequirement>()
              : new List<OpenApiSecurityRequirement>
              {
                new()
                {
                  { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "eva-auth" } }, Array.Empty<string>() }
                }
              },
            RequestBody = new OpenApiRequestBody
            {
              Description = "",
              Required = true,
              Content = new Dictionary<string, OpenApiMediaType>
              {
                { "application/json", new OpenApiMediaType { Schema = ToSchema(service.RequestTypeID) } }
              }
            },
            Responses = new OpenApiResponses
            {
              {
                "200", new OpenApiResponse
                {
                  Description = $"The response for a call to {service.Name}",
                  Content = new Dictionary<string, OpenApiMediaType>
                  {
                    { "application/json", new OpenApiMediaType { Schema = ToSchema(service.ResponseTypeID), } }
                  }
                }
              },
              {
                "4XX", new OpenApiResponse
                {
                  Description = "A BadRequest response",
                  Content = new Dictionary<string, OpenApiMediaType>
                  {
                    {
                      "application/json", new OpenApiMediaType
                      {
                        Schema = new OpenApiSchema { Reference = new OpenApiReference { Id = Schema_Error, Type = ReferenceType.Schema } },
                        Examples = new Dictionary<string, OpenApiExample>
                        {
                          { "RequestValidationFailure", new OpenApiExample { Reference = new OpenApiReference { Type = ReferenceType.Example, Id = Example_400_RequestValidation } } },
                          { "Forbidden", new OpenApiExample { Reference = new OpenApiReference { Type = ReferenceType.Example, Id = Example_403_Forbidden } } }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
      }
    };
  }

  private static bool SupportsExternalIdsMode(State state, ApiDefinitionModel input, string s)
  {
    if (state.SupportsBackendIDCache.TryGetValue(s, out var cached)) return cached;

    var result = SupportsExternalIdsMode_Uncached(input, s, new HashSet<string>());
    state.SupportsBackendIDCache[s] = result;
    return result;
  }

  private static bool SupportsExternalIdsMode_Uncached(ApiDefinitionModel input, string s, HashSet<string> recursionGuard)
  {
    if (recursionGuard.Contains(s)) return false;

    recursionGuard.Add(s);
    var type = input.Types[s];
    var result = type.Properties.Any(p => p.Value.DataModelInformation is { SupportsBackendID: true }) || type.TypeDependencies.Any(d => SupportsExternalIdsMode_Uncached(input, d, recursionGuard));
    recursionGuard.Remove(s);
    return result;
  }

  private static string FixName(string name)
  {
    return NameRegex().Replace(name, "_").Trim('_');
  }

  private static string TagFromAssembly(string name)
  {
    return name.StartsWith("EVA.") ? name[4..] : name;
  }

  [GeneratedRegex("[^a-zA-Z0-9-._]", RegexOptions.Compiled)]
  private static partial Regex NameRegex();
}