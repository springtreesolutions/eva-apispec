﻿using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace EVA.Core.Typings.V2;

public class AuthInformation
{
  [JsonPropertyName("allowPublic")] public bool AllowPublic { get; set; }
  [JsonPropertyName("allowLimitedTrust")] public bool AllowLimitedTrust { get; set; }
  [JsonPropertyName("requiredPermissions")] public ImmutableArray<PermissionInformation> RequiredPermissions { get; set; } = ImmutableArray<PermissionInformation>.Empty;
}