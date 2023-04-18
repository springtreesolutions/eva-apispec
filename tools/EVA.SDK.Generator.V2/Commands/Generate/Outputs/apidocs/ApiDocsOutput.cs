﻿using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using EVA.API.Spec;

namespace EVA.SDK.Generator.V2.Commands.Generate.Outputs.apidocs;

internal class ApiDocsOutput : IOutput<ApiDocsOptions>
{
  public string? OutputPattern => null;
  public string[] ForcedRemoves => new[] { "generics", "options", "inheritance" };

  public async Task Write(OutputContext<ApiDocsOptions> ctx)
  {
    // List of all services
    await GenerateSidebar(ctx);

    // Generate each service
    foreach (var service in ctx.Input.Services)
    {
      await GenerateService(ctx, service);
    }

    // Generate typesense output
    await GenerateTypesense(ctx);
  }

  private async Task GenerateTypesense(OutputContext<ApiDocsOptions> ctx)
  {
    var output = new StringBuilder();

    foreach (var service in ctx.Input.Services)
    {
      var requestType = ctx.Input.Types[service.RequestTypeID];

      var o = new RootObject
      {
        anchor = service.Name.ToLowerInvariant(),
        content = requestType.Description,
        content_camel = requestType.Description,
        docusaurus_tag = "docs-default-current",
        hierarchy = new Hierarchy { lvl0 = "Developers", lvl1 = service.Name },
        hierarchy_lvl0 = "Developers",
        hierarchy_lvl1 = service.Name,
        hierarchy_camel = new[] { new Hierarchy_camel { lvl0 = "Developers", lvl1 = service.Name } },
        hierarchy_radio = new Hierarchy_radio(),
        hierarchy_radio_camel = new Hierarchy_radio_camel(),
        item_priority = 75,
        language = "en",
        no_variables = true,
        tags = new[] { "login", "nb", "dev" },
        type = "content",
        url = $"https://docs.newblack.io/documentation/api-reference/{service.Name}",
        url_without_anchor = $"https://docs.newblack.io/documentation/api-reference/{service.Name}",
        url_without_variables = $"https://docs.newblack.io/documentation/api-reference/{service.Name}",
        version = new[] { "current" },
        weight = new Weight { level = 0, page_rank = 0, position = 75 }
      };

      output.AppendLine(JsonSerializer.Serialize(o, JsonContext.Default.RootObject));
    }

    ctx.Writer.WriteFileAsync("typesense.ndjson", output.ToString());
  }

  private static async Task GenerateService(OutputContext<ApiDocsOptions> ctx, ServiceModel service)
  {
    var model = new ServiceItem
    {
      Name = service.Name,
      Method = "POST",
      Path = service.Path,
      Request = new ServiceItem.RequestItem
      {
        Properties = new List<ServiceItem.RequestPropertyItem>()
      },
      Response = new ServiceItem.ResponseItem
      {
        Properties = new List<ServiceItem.ResponsePropertyItem>()
      }
    };

    // Request type
    var requestType = ctx.Input.Types[service.RequestTypeID];
    model.Description = requestType.Description;

    model.Request.Properties = FillRecursiveProperties<ServiceItem.RequestPropertyItem>(ctx.Input, new TypeReference(service.RequestTypeID, ImmutableArray<TypeReference>.Empty, false), x =>
    {
      return new ServiceItem.RequestPropertyItem
      {
        Name = x.name,
        Description = x.property.Description,
        Type = ToTypeName(x.property.Type),
        Properties = x.nestedProperties,
        Recursion = x.nestedProperties is { Count: 0 },
        EnumValues = x.enumValues
      };
    })!;

    // Response type
    model.Response.Properties = FillRecursiveProperties<ServiceItem.ResponsePropertyItem>(ctx.Input, new TypeReference(service.ResponseTypeID, ImmutableArray<TypeReference>.Empty, false), x =>
    {
      return new ServiceItem.ResponsePropertyItem()
      {
        Name = x.name,
        Description = x.property.Description,
        Type = ToTypeName(x.property.Type),
        Properties = x.nestedProperties,
        Recursion = x.nestedProperties is { Count: 0 },
        EnumValues = x.enumValues
      };
    })!;

    await ctx.Writer.WriteFileAsync($"services/{model.Name}.json", JsonSerializer.Serialize(model, JsonContext.Indented.ServiceItem));
  }

  private static string ToTypeName(TypeReference spec)
  {
    var n = spec.Nullable ? "?" : "";

    if (spec.Name == ApiSpecConsts.Specials.Array)
    {
      return $"{ToTypeName(spec.Arguments[0])}[]{n}";
    }

    if (spec.Name == ApiSpecConsts.Specials.Map)
    {
      return $"{{{ToTypeName(spec.Arguments[0])}: {ToTypeName(spec.Arguments[1])}}}{n}";
    }

    return spec.Name;
  }

  private static async Task GenerateSidebar(OutputContext<ApiDocsOptions> ctx)
  {
    var sidebar = new List<SidebarItem>();

    foreach (var service in ctx.Input.Services)
    {
      sidebar.Add(new SidebarItem
      {
        Type = "doc",
        Label = service.Name,
        ClassName = "api-method post",
        ID = $"api-reference/{service.Name}",
        Link = $"api-reference/{service.Name}"
      });
    }

    await ctx.Writer.WriteFileAsync("sidebar.json", JsonSerializer.Serialize(sidebar.ToArray(), JsonContext.Default.SidebarItemArray));
  }

  private static Dictionary<string, long>? GetEnumValues(ApiDefinitionModel input, TypeReference? typeReference)
  {
    if (typeReference == null) return null;

    var propTypeName = typeReference.Name;

    // Arrays, we just recurse
    if (propTypeName == ApiSpecConsts.Specials.Array)
    {
      return GetEnumValues(input, typeReference.Arguments[0]);
    }

    // Option, we only expose the shared properties
    if (propTypeName == ApiSpecConsts.Specials.Option)
    {
      return GetEnumValues(input, typeReference.Shared);
    }

    // Primitives don't have properties
    if (ApiSpecConsts.AllPrimitives.Contains(propTypeName))
    {
      return null;
    }

    // TODO: Maps don't have properties (for now)
    if (propTypeName == ApiSpecConsts.Specials.Map)
    {
      return null;
    }

    var type = input.Types[propTypeName];
    if (type.EnumIsFlag == null)
    {
      return null;
    }

    long TotalValue(string name)
    {
      var value = type.EnumValues[name];
      if (value == null) return 0;
      return value.Value + value.Extends.Sum(TotalValue);
    }

    return type.EnumValues.ToDictionary(x => x.Key, x => TotalValue(x.Key));
  }

  private static List<TProperty>? FillRecursiveProperties<TProperty>(
    ApiDefinitionModel input,
    TypeReference? typeReference,
    Func<(string name, PropertySpecification property, List<TProperty>? nestedProperties, Dictionary<string, long>? enumValues), TProperty> propertyBuilder,
    Stack<string>? recursionGuard = null)
  {
    if (typeReference == null) return null;

    var propTypeName = typeReference.Name;

    // Arrays, we just recurse
    if (propTypeName == ApiSpecConsts.Specials.Array)
    {
      return FillRecursiveProperties(input, typeReference.Arguments[0], propertyBuilder, recursionGuard);
    }

    // Option, we only expose the shared properties
    if (propTypeName == ApiSpecConsts.Specials.Option)
    {
      return FillRecursiveProperties(input, typeReference.Shared, propertyBuilder, recursionGuard);
    }

    // Primitives don't have properties
    if (ApiSpecConsts.AllPrimitives.Contains(propTypeName))
    {
      return null;
    }

    // TODO: Maps don't have properties (for now)
    if (propTypeName == ApiSpecConsts.Specials.Map)
    {
      return null;
    }

    var type = input.Types[propTypeName];
    var properties = new List<TProperty>();

    recursionGuard ??= new Stack<string>();
    if (recursionGuard.Contains(propTypeName)) return properties;
    recursionGuard.Push(propTypeName);

    foreach (var (propName, propValue) in type.Properties)
    {
      // Figure out the nested properties for the type
      var nestedProperties = FillRecursiveProperties(input, propValue.Type, propertyBuilder, recursionGuard);

      // Figure out the enum values for the type
      var enumValues = GetEnumValues(input, propValue.Type);

      var property = propertyBuilder((propName, propValue, nestedProperties, enumValues));
      properties.Add(property);
    }

    recursionGuard.Pop();
    return properties.Count == 0 ? null : properties;
  }
}

internal class SidebarItem
{
  [JsonPropertyName("type")] public string Type { get; set; }
  [JsonPropertyName("id")] public string ID { get; set; }
  [JsonPropertyName("link")] public string Link { get; set; }
  [JsonPropertyName("label")] public string Label { get; set; }
  [JsonPropertyName("className")] public string ClassName { get; set; }
}

internal class ServiceItem
{
  [JsonPropertyName("name")] public string Name { get; set; }
  [JsonPropertyName("description")] public string? Description { get; set; }
  [JsonPropertyName("method")] public string Method { get; set; }
  [JsonPropertyName("path")] public string Path { get; set; }
  [JsonPropertyName("request")] public RequestItem Request { get; set; }
  [JsonPropertyName("response")] public ResponseItem Response { get; set; }

  public class RequestItem
  {
    [JsonPropertyName("properties")] public List<RequestPropertyItem> Properties { get; set; }
  }

  public class ResponseItem
  {
    [JsonPropertyName("properties")] public List<ResponsePropertyItem> Properties { get; set; }
  }

  public class RequestPropertyItem
  {
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("properties")] public List<RequestPropertyItem>? Properties { get; set; }
    [JsonPropertyName("recursion")] public bool Recursion { get; set; }
    [JsonPropertyName("enumValues")] public Dictionary<string, long>? EnumValues { get; set; }
  }

  public class ResponsePropertyItem
  {
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; }
    [JsonPropertyName("properties")] public List<ResponsePropertyItem>? Properties { get; set; }
    [JsonPropertyName("recursion")] public bool Recursion { get; set; }
    [JsonPropertyName("enumValues")] public Dictionary<string, long>? EnumValues { get; set; }
  }
}