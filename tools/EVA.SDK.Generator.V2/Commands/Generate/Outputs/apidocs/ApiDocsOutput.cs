﻿using System.Collections.Immutable;
using System.Text.Json;
using EVA.API.Spec;
using EVA.SDK.Generator.V2.Commands.Generate.Transforms;

namespace EVA.SDK.Generator.V2.Commands.Generate.Outputs.apidocs;

internal class ApiDocsOutput : IOutput<ApiDocsOptions>
{
  private class State
  {
    internal readonly Dictionary<string, (bool backendID, bool systemID)> SupportsBackendIDCache = new();
    internal readonly Dictionary<string, string> TypeMapping = new();
    internal int nextID = 1;

    internal void ResetForService()
    {
      TypeMapping.Clear();
      nextID = 1;
    }
  }

  public string? OutputPattern => null;
  public string[] ForcedTransformations => [RemoveGenerics.ID, RemoveInheritance.ID, RemoveDataLakeExports.ID, RemoveEventExports.ID, RemoveErrors.ID];

  public async Task Write(OutputContext<ApiDocsOptions> ctx)
  {
    var state = new State();

    await GenerateSidebar(ctx);

    foreach (var service in ctx.Input.Services)
    {
      state.ResetForService();
      await GenerateService(state, service, ctx);
    }
  }

  private static async Task GenerateSidebar(OutputContext<ApiDocsOptions> ctx)
  {
    var entries = ctx.Input.Services
      .Select(service => new ServiceIndex.Entry
      {
        Name = service.Name,
        Method = service.Method ?? "POST"
      })
      .OrderBy(x => x.Name).ToImmutableArray();

    var sidebar = new ServiceIndex
    {
      Entries = entries
    };

    await ctx.Writer.WriteFileAsync("eva/index.json", JsonSerializer.Serialize(sidebar, JsonContext.Default.ServiceIndex));
  }

  private static async Task GenerateService(State state, ServiceModel service, OutputContext<ApiDocsOptions> ctx)
  {
    var result = new SingleService
    {
      Name = service.Name,
      Path = service.Path,
      Method = service.Method ?? "POST",
      Types = new Dictionary<string, List<TypeInfo>>()
    };

    var requestType = ctx.Input.Types[service.RequestTypeID];
    var supportsExternalID = SupportsExternalIdsMode(state, ctx.Input, service.RequestTypeID);

    result.Description = requestType.Description ?? $"The {service.Name} service";

    if (service.Deprecated is { } deprecated)
    {
      result.Deprecation = $"\n\n**Deprecated since {deprecated.Introduced}:** {deprecated.Comment}\n\n**Will be removed from the typings in {deprecated.Effective}**";
    }

    result.RequestTypeID = BuildTypeInfo(state, result.Types, ctx, service.RequestTypeID);
    result.ResponseTypeID = BuildTypeInfo(state, result.Types, ctx, service.ResponseTypeID);

    // Headers
    result.Headers =
    [
      new Header
      {
        Name = "EVA-User-Agent",
        Type = "string",
        Description = "The user agent that is making these calls. Don't make this specific per device/browser but per application. This should be of the form: `MyFirstUserAgent/1.0.0`",
        DefaultValue = null,
        Required = true
      },
      new Header
      {
        Name = "EVA-Requested-OrganizationUnitID",
        Type = "integer",
        Description = "The ID of the organization unit to run this request in.",
        DefaultValue = null,
        Required = false
      },
      new Header
      {
        Name = "EVA-Requested-OrganizationUnit-Query",
        Type = "string",
        Description = "The query that selects the organization unit to run this request in.",
        DefaultValue = null,
        Required = false
      }
    ];

    if (supportsExternalID.backendID)
    {
      result.Headers.Add(new Header
      {
        Name = "EVA-IDs-Mode",
        Type = "string",
        Description = "The IDs mode to run this request in. Currently only `ExternalIDs` is supported.",
        DefaultValue = null,
        Required = false
      });
    }

    if (supportsExternalID.systemID)
    {
      result.Headers.Add(new Header
      {
        Name = "EVA-IDs-BackendSystemID",
        Type = "string",
        Description = "The ID of the backend system that is used to resolve the IDs.",
        DefaultValue = null,
        Required = false
      });
    }

    // Request samples
    result.RequestSamples =
    [
      new RequestSample
      {
        Name = "CURL",
        Sample = "# Coming soon\n# Very soon...",
        Syntax = "bash"
      }
    ];

    // Response samples
    result.ResponseSamples =
    [
      new ResponseSample
      {
        Name = "200",
        Sample = "{}"
      },
      new ResponseSample
      {
        Name = "400",
        Sample =
          "{\n  \"Error\": {\n    \"Code\": \"COVFEFE\",\n    \"Type\": \"RequestValidationFailure\",\n    \"Message\": \"Validation of the request message failed: Field ABC has an invalid value for a Product.\",\n    \"RequestID\": \"576b62dd71894e3281a4d84951f44e70\"\n  }\n}"
      },
      new ResponseSample
      {
        Name = "403",
        Sample =
          "{\n  \"Error\": {\n    \"Code\": \"COVFEFE\",\n    \"Type\": \"Forbidden\",\n    \"Message\": \"You are not authorized to execute this request.\",\n    \"RequestID\": \"576b62dd71894e3281a4d84951f44e70\"\n  }\n}"
      }
    ];

    await ctx.Writer.WriteFileAsync($"eva/services/{service.Name}.json", JsonSerializer.Serialize(result, JsonContext.Indented.SingleService));
  }

  private static string BuildTypeInfo(State state, Dictionary<string, List<TypeInfo>> types, OutputContext<ApiDocsOptions> ctx, string typeID)
  {
    if (state.TypeMapping.TryGetValue(typeID, out var mapped))
    {
      return mapped;
    }

    var id = ToBase26(state.nextID++);
    state.TypeMapping[typeID] = id;

    var result = types[id] = [];
    var type = ctx.Input.Types[typeID];

    foreach (var (propname, prop) in type.Properties)
    {
      var ti = new TypeInfo
      {
        Name = propname,
        Required = prop.Required?.CurrentValue ?? false,
        Type = "unknown",
        Description = prop.Description ?? string.Empty
      };

      (ti.Type, ti.PropertiesID, var options) = ToType(state, types, ctx, prop.Type);
      ti.OneOf = options?.ToArray();
      result.Add(ti);
    }

    return id;
  }

  private static (string type, string? properties, List<OneOf>? oneof) ToType(State state, Dictionary<string, List<TypeInfo>> types, OutputContext<ApiDocsOptions> ctx, TypeReference type,
    bool? forceNullable = null)
  {
    string? properties = null;
    List<OneOf>? oneof = null;

    var result = type.Name switch
    {
      ApiSpecConsts.Bool => "boolean",
      ApiSpecConsts.Int16 or ApiSpecConsts.Int32 or ApiSpecConsts.Int64 => "integer",
      ApiSpecConsts.Float32 or ApiSpecConsts.Float64 or ApiSpecConsts.Float128 => "float",
      ApiSpecConsts.Guid => "guid",
      ApiSpecConsts.Date => "datetime",
      ApiSpecConsts.String => "string",
      ApiSpecConsts.Binary => "binary",
      ApiSpecConsts.Any => "any",
      ApiSpecConsts.Duration => "duration",
      ApiSpecConsts.Object => "object",
      _ => null
    };

    if (type.Name is ApiSpecConsts.Specials.Map)
    {
      var (s1, s2, _) = ToType(state, types, ctx, type.Arguments[1], false);

      result = $"map[{s1}]";
      properties = s2;
    }
    else if (type.Name is ApiSpecConsts.Specials.Option)
    {
      var (s1, s2, _) = ToType(state, types, ctx, type.Shared!, false);

      result = $"one-of[{s1}]";
      properties = s2;

      oneof = new List<OneOf>();
      foreach (var x in type.Arguments)
      {
        var (_, x2, _) = ToType(state, types, ctx, x, false);
        oneof.Add(new OneOf { Name = x.Name[(x.Name.LastIndexOf('.') + 1)..], PropertiesID = x2 });
      }
    }
    else if (type.Name is ApiSpecConsts.Specials.Array)
    {
      var (s1, s2, _) = ToType(state, types, ctx, type.Arguments[0], false);

      result = $"array[{s1}]";
      properties = s2;
    }
    else if (result == null)
    {
      result = "object";
      properties = BuildTypeInfo(state, types, ctx, type.Name);
    }

    if (forceNullable is true || !forceNullable.HasValue && type.Nullable)
    {
      result += " | null";
    }

    return (result, properties, oneof);
  }

  private static string ToBase26(int x)
  {
    var result = string.Empty;
    while (x > 0)
    {
      result = (char)('A' + x % 26) + result;
      x /= 26;
    }

    return result;
  }

  private static (bool backendID, bool systemID) SupportsExternalIdsMode(State state, ApiDefinitionModel input, string s)
  {
    if (state.SupportsBackendIDCache.TryGetValue(s, out var cached)) return cached;

    var result = SupportsExternalIdsMode_Uncached(input, s, new HashSet<string>());
    state.SupportsBackendIDCache[s] = result;
    return result;
  }

  private static (bool backendID, bool systemID) SupportsExternalIdsMode_Uncached(ApiDefinitionModel input, string s, HashSet<string> recursionGuard)
  {
    if (!recursionGuard.Add(s)) return (false, false);

    var type = input.Types[s];

    var backendID = type.Properties.Any(p =>
                      p.Value.DataModelInformation is { SupportsBackendID: true })
                    || type.TypeDependencies.Any(d => SupportsExternalIdsMode_Uncached(input, d, recursionGuard).backendID);

    var systemID = type.Properties.Any(p =>
                     p.Value.DataModelInformation is { SupportsSystemID: true })
                   || type.TypeDependencies.Any(d => SupportsExternalIdsMode_Uncached(input, d, recursionGuard).systemID);

    recursionGuard.Remove(s);
    return (backendID, systemID);
  }
}