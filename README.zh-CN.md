**中文** | [English](README.MD)


# FreeSql.Extensions.JsonMap.STJ

它是 FreeSql 扩展包，基于 `System.Text.Json` 将值对象映射成 `typeof(string)` 类型。

> **注意**：与官方 `FreeSql.Extensions.JsonMap` 版本相比，当前版本底层已从 `Newtonsoft.Json` 迁移至 `System.Text.Json`，不再支持 .NET Framework 4.0/4.5。最低要求 .NET Standard 2.0。

### 安装

> dotnet add package FreeSql.Extensions.JsonMap.STJ

### 快速开始

```
// 开启功能（使用默认配置：忽略大小写、允许中文不转义）
fsql.UseJsonMap(); 
```

### 高级配置

如果需要自定义序列化行为（如驼峰命名、缩进等），可以传入 `JsonSerializerOptions`：

```
using System.Text.Json;

// 自定义配置
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // 驼峰命名
    WriteIndented = true,                              // 格式化输出
    IgnoreReadOnlyProperties = true
};

fsql.UseJsonMap(options);
```

### 使用示例

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

    // 当属性类型为泛型或对象时，标记 [JsonMap] 即可自动序列化存储
    [JsonMap]
    public T Config { get; set; }
}
```
