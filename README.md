[中文](README.zh-CN.md) | **English**

# FreeSql.Extensions.JsonMap.STJ

This is a FreeSql extension package that maps value objects to `typeof(string)` based on `System.Text.Json`.

> **Note**: Compared to the official `FreeSql.Extensions.JsonMap` version, the underlying implementation of this version has been migrated from `Newtonsoft.Json` to `System.Text.Json`. It no longer supports .NET Framework 4.0/4.5. The minimum requirement is .NET Standard 2.0.

### Installation

> dotnet add package FreeSql.Extensions.JsonMap.STJ

### Quick Start

```
// Enable feature (Use default configuration: case insensitive, allows Chinese without escaping)
fsql.UseJsonMap(); 
```

### Advanced Configuration

If you need to customize serialization behavior (such as camelCase naming, indentation, etc.), you can pass in `JsonSerializerOptions`:

```
using System.Text.Json;

// Custom configuration
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // CamelCase naming
    WriteIndented = true,                              // Formatted output
    IgnoreReadOnlyProperties = true
};

fsql.UseJsonMap(options);
```

### Usage Example

```
class TestConfig
{
    public int clicks { get; set; }
    public string title { get; set; }
}

[Table(Name = "sysconfig")]
public class S_SysConfig<T>
{
    [Column(IsPrimary = true)]
    public string Name { get; set; }

    // When the property type is a generic or an object, mark it with [JsonMap] to automatically serialize it for storage
    [JsonMap]
    public T Config { get; set; }
}
```