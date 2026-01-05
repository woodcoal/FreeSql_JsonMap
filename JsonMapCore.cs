using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Threading;
using FreeSql;
using FreeSql.DataAnnotations;
using FreeSql.Internal.CommonProvider;

/// <summary>
/// FreeSql JsonMap 扩展核心类
/// <para>提供将对象属性映射为数据库 JSON 字段存储的功能，使用 System.Text.Json 进行序列化处理。</para>
/// </summary>
public static class FreeSqlJsonMapCoreExtensions {

	// 防止重复注册 AOP 事件的标志位
	private static int _IsAoped = 0;

	// 缓存已启用 JsonMap 的类型，避免重复处理
	private static readonly ConcurrentDictionary<Type, bool> _DicTypes = new ConcurrentDictionary<Type, bool>();

	#region System.Text.Json 反射方法缓存
	// 缓存 System.Text.Json.JsonSerializer 的方法元数据，用于后续表达式树构建

	// 获取 Deserialize 方法信息: public static object? Deserialize(string json, Type returnType, JsonSerializerOptions? options = null)
	// 使用反射查找是为了在运行时动态构建调用，因为泛型方法在表达式树中构建较为复杂，这里选择非泛型重载
	private static readonly MethodInfo _MethodJsonConvertDeserializeObject = typeof(JsonSerializer)
		.GetMethods(BindingFlags.Public | BindingFlags.Static)
		.FirstOrDefault(m => m.Name == "Deserialize" &&
							 m.IsGenericMethod == false &&
							 m.GetParameters().Length == 3 &&
							 m.GetParameters()[0].ParameterType == typeof(string) &&
							 m.GetParameters()[1].ParameterType == typeof(Type) &&
							 m.GetParameters()[2].ParameterType == typeof(JsonSerializerOptions));

	// 获取 Serialize 方法信息: public static string Serialize(object? value, Type inputType, JsonSerializerOptions? options = null)
	// 显式指定 inputType 参数对于多态序列化非常重要，确保派生类属性被正确包含
	private static readonly MethodInfo _MethodJsonConvertSerializeObject = typeof(JsonSerializer)
		.GetMethods(BindingFlags.Public | BindingFlags.Static)
		.FirstOrDefault(m => m.Name == "Serialize" &&
							 m.IsGenericMethod == false &&
							 m.GetParameters().Length == 3 &&
							 m.GetParameters()[0].ParameterType == typeof(object) &&
							 m.GetParameters()[1].ParameterType == typeof(Type) &&
							 m.GetParameters()[2].ParameterType == typeof(JsonSerializerOptions));
	#endregion

	// 缓存 Fluent API 配置的 JsonMap 属性信息
	private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, bool>> _DicJsonMapFluentApi = new ConcurrentDictionary<Type, ConcurrentDictionary<string, bool>>();

	// 锁对象，用于线程安全的字典操作
	private static readonly object _ConcurrentObj = new object();

	/// <summary>
	/// Fluent API 方式开启属性的 JsonMap 映射
	/// </summary>
	public static ColumnFluent JsonMap(this ColumnFluent col) {
		_DicJsonMapFluentApi.GetOrAdd(col._entityType, et => new ConcurrentDictionary<string, bool>())
			.GetOrAdd(col._property.Name, pn => true);
		return col;
	}

	/// <summary>
	/// 当实体类属性为 <see cref="object"/> 且标记了 <see cref="JsonMapAttribute"/> 特性时，以 JSON 形式映射存储。
	/// <br />
	/// 默认配置：启用宽松的字符编码（防止中文被转义为 Unicode 编码）和忽略属性名大小写。
	/// </summary>
	public static void UseJsonMap(this IFreeSql fsql) =>
		UseJsonMap(fsql, new JsonSerializerOptions {
			PropertyNameCaseInsensitive = true,
			Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) // 允许所有字符不转义，对中文友好
		});

	/// <summary>
	/// 使用自定义的 JsonSerializerOptions 开启 JsonMap 功能
	/// </summary>
	public static void UseJsonMap(this IFreeSql fsql, JsonSerializerOptions settings) {
		// 确保全局只注册一次 AOP 事件，防止重复执行
		if (Interlocked.CompareExchange(ref _IsAoped, 1, 0) == 0) {

			// 注册【读取】拦截器：当从数据库读取数据映射到实体属性时触发
			FreeSql.Internal.Utils.GetDataReaderValueBlockExpressionSwitchTypeFullName.Add((returnTarget, valueExp, type) => {
				// 如果该类型是被标记为 JsonMap 的类型
				if (_DicTypes.ContainsKey(type)) {
					// 构建表达式树：
					// if (valueExp is Type) return valueExp; 
					// else return JsonSerializer.Deserialize((string)valueExp, Type, settings);
					return Expression.IfThenElse(
						Expression.TypeIs(valueExp, type),
						Expression.Return(returnTarget, valueExp),
						Expression.Return(returnTarget,
							Expression.TypeAs(
								Expression.Call(_MethodJsonConvertDeserializeObject,
									Expression.Convert(valueExp, typeof(string)),
									Expression.Constant(type),
									Expression.Constant(settings)),
								type))
					);
				}
				return null;
			});
		}

		// 注册实体属性配置事件，用于识别和标记 JsonMap 属性
		fsql.Aop.ConfigEntityProperty += (s, e) => {
			// 判断属性是否标记了 [JsonMap] 特性 或 通过 FluentAPI 配置
			var isJsonMap = e.Property.GetCustomAttributes(typeof(JsonMapAttribute), false).Any() ||
						   (_DicJsonMapFluentApi.TryGetValue(e.EntityType, out var tryjmfu) && tryjmfu.ContainsKey(e.Property.Name));

			if (isJsonMap) {
				// 忽略 FreeSql 内部已处理的基础类型（如 int, string 等），JsonMap 仅针对复杂对象
				if (_DicTypes.ContainsKey(e.Property.PropertyType) == false &&
					FreeSql.Internal.Utils.dicExecuteArrayRowReadClassOrTuple.ContainsKey(e.Property.PropertyType)) {
					return;
				}

				// 设置数据库映射类型
				if (e.ModifyResult.MapType == null) {
					switch (fsql.Ado.DataType) {
						case DataType.PostgreSQL:
							e.ModifyResult.MapType = typeof(JsonObject);
							break;
						default:
							// 其他数据库默认映射为长字符串 (NVARCHAR/TEXT/LONGTEXT)
							e.ModifyResult.MapType = typeof(string);
							e.ModifyResult.StringLength = -2; // -2 表示使用该数据库的最大长度类型
							break;
					}
				}

				// 将该类型加入缓存，并注册写入拦截器
				if (_DicTypes.TryAdd(e.Property.PropertyType, true)) {
					lock (_ConcurrentObj) {
						// 标记该类型需要特殊处理
						FreeSql.Internal.Utils.dicExecuteArrayRowReadClassOrTuple[e.Property.PropertyType] = true;

						// 注册【写入】拦截器：当实体属性值写入到 SQL 参数时触发 (Object -> String/JSON)
						FreeSql.Internal.Utils.GetDataReaderValueBlockExpressionObjectToStringIfThenElse.Add((returnTarget, valueExp, elseExp, type) => {
							// 构造序列化调用表达式: JsonSerializer.Serialize(value, type, settings)
							var serializeCall = Expression.Call(_MethodJsonConvertSerializeObject,
								Expression.Convert(valueExp, typeof(object)),
								Expression.Constant(e.Property.PropertyType), // 传入实际运行时类型
								Expression.Constant(settings));

							// 构建表达式树：
							// if (value is PropertyType) return JsonSerializer.Serialize(...);
							// else execute_else_logic;
							return Expression.IfThenElse(
								Expression.TypeIs(valueExp, e.Property.PropertyType),
								// 注意：这里必须将 string 结果转回 object，以匹配委托的 returnTarget 签名
								Expression.Return(returnTarget, Expression.Convert(serializeCall, typeof(object))),
								elseExp);
						});
					}
				}
			}
		};

		// 注册表达式解析事件：用于将 C# LINQ 表达式翻译成 SQL 中的 JSON 查询函数
		// 例如：.Where(x => x.Config.Title == "Test") -> json_extract(config, '$.Title') = 'Test'
		switch (fsql.Ado.DataType) {
			case DataType.Sqlite:
			case DataType.MySql:
			case DataType.OdbcMySql:
			case DataType.CustomMySql:
			case DataType.SqlServer:
			case DataType.OdbcSqlServer:
			case DataType.CustomSqlServer:
			case DataType.Oracle:
			case DataType.OdbcOracle:
			case DataType.CustomOracle:
			case DataType.Dameng:
			case DataType.DuckDB:
			case DataType.PostgreSQL:
			case DataType.OdbcPostgreSQL:
			case DataType.CustomPostgreSQL:
			case DataType.KingbaseES:
			case DataType.ShenTong:
				fsql.Aop.ParseExpression += (_, e) => {
					// 处理 MySQL 特有的 Enum -> Int 转换逻辑 (JsonMap 之外的通用逻辑，保留原样)
					switch (fsql.Ado.DataType) {
						case DataType.MySql:
						case DataType.OdbcMySql:
						case DataType.CustomMySql:
							if (e.Expression.NodeType == ExpressionType.Equal && e.Expression is BinaryExpression binaryExpression) {
								var comonExp = (fsql.Select<object>() as Select0Provider)._commonExpression;
								var leftExp = binaryExpression.Left;
								var rightExp = binaryExpression.Right;
								if (leftExp.NodeType == ExpressionType.Convert && leftExp is UnaryExpression leftExpUexp && leftExpUexp.Operand?.Type.NullableTypeOrThis().IsEnum == true &&
									rightExp.NodeType == ExpressionType.Convert && rightExp is UnaryExpression rightExpUexp && rightExpUexp.Operand?.Type.NullableTypeOrThis().IsEnum == true) {
									string leftSql = null, rightSql = null;
									if (leftExpUexp.Operand.NodeType == ExpressionType.MemberAccess && LocalParseMemberExp(leftExpUexp.Operand as MemberExpression)) {
										leftSql = e.Result;
									}

									if (rightExpUexp.Operand.NodeType == ExpressionType.MemberAccess && LocalParseMemberExp(rightExpUexp.Operand as MemberExpression)) {
										rightSql = e.Result;
									}

									if (!string.IsNullOrEmpty(leftSql) && string.IsNullOrEmpty(rightSql) && !rightExpUexp.Operand.IsParameter()) {
										rightSql = comonExp.formatSql(Expression.Lambda(rightExpUexp.Operand).Compile().DynamicInvoke(), typeof(int), null, null);
									}

									if (string.IsNullOrEmpty(leftSql) && !string.IsNullOrEmpty(rightSql) && !leftExpUexp.Operand.IsParameter()) {
										leftSql = comonExp.formatSql(Expression.Lambda(leftExpUexp.Operand).Compile().DynamicInvoke(), typeof(int), null, null);
									}

									if (!string.IsNullOrEmpty(leftSql) && !string.IsNullOrEmpty(rightSql)) {
										e.Result = $"{leftSql} = {rightSql}";
										return;
									}
									e.Result = null;
									return;
								}
							}
							// 处理 MySQL JSON 数组查询: List.Contains -> JSON_CONTAINS 或 IN (...)
							if (e.Expression.NodeType == ExpressionType.Call && e.Expression is MethodCallExpression callExp && callExp.Method.Name == "Contains") {
								var objExp = callExp.Object;
								var objType = objExp?.Type;
								if (objType?.FullName == "System.Byte[]") {
									return;
								}

								var argIndex = 0;
								if (objType == null && callExp.Method.DeclaringType == typeof(Enumerable)) {
									objExp = callExp.Arguments.FirstOrDefault();
									objType = objExp?.Type;
									argIndex++;
									if (objType == typeof(string)) {
										return;
									}
								}
								if (objType == null) {
									objType = callExp.Method.DeclaringType;
								}

								if (objType != null || objType.IsArrayOrList()) {
									var memExp = callExp.Arguments[argIndex];
									if (memExp.NodeType == ExpressionType.MemberAccess && memExp.Type.NullableTypeOrThis().IsEnum && LocalParseMemberExp(memExp as MemberExpression)) {
										if (!objExp.IsParameter()) {
											var comonExp = (fsql.Select<object>() as Select0Provider)._commonExpression;
											var rightSql = comonExp.formatSql(Expression.Lambda(objExp).Compile().DynamicInvoke(), typeof(int), null, null);
											e.Result = $"{e.Result} in {rightSql.Replace(",   \r\n    \r\n", $") \r\n OR {e.Result} in (")}";
											return;
										}
										e.Result = null;
										return;
									}
								}
							}
							break;
					}

					// 核心逻辑：解析 POCO 属性访问，判断是否访问了 JSON 字段内部属性
					// 例如: a.Info.Name -> 解析为 JSON 路径
					if (e.Expression.NodeType == ExpressionType.MemberAccess) {
						LocalParseMemberExp(e.Expression as MemberExpression);
					}

					// 本地递归解析函数
					bool LocalParseMemberExp(MemberExpression memExp) {
						if (memExp == null) {
							return false;
						}

						if (e.Expression.IsParameter() == false) {
							return false;
						}

						// 使用栈来保存属性访问链，例如 a.Info.User.Name，入栈顺序: Name -> User -> Info
						var parentMemExps = new Stack<MemberExpression>();
						parentMemExps.Push(memExp);
						while (true) {
							switch (memExp.Expression.NodeType) {
								case ExpressionType.MemberAccess:
								case ExpressionType.Parameter:
									break;
								default:
									return false; // 遇到不支持的节点类型，停止解析
							}
							switch (memExp.Expression.NodeType) {
								case ExpressionType.MemberAccess:
									// 继续向上遍历：a.Info.User.Name -> a.Info.User
									memExp = memExp.Expression as MemberExpression;
									if (memExp == null) {
										return false;
									}

									parentMemExps.Push(memExp);
									break;
								case ExpressionType.Parameter:
									// 到达根参数 'a'，开始判断最顶层属性是否为 JsonMap 属性
									var tb = fsql.CodeFirst.GetTableByEntity(memExp.Expression.Type);
									if (tb == null) {
										return false;
									}

									// 检查栈顶属性（例如 Info）是否在表中存在且被标记为 JsonMap 类型
									if (tb.ColumnsByCs.TryGetValue(parentMemExps.Pop().Member.Name, out var trycol) == false) {
										return false;
									}

									if (_DicTypes.ContainsKey(trycol.CsType) == false) {
										return false;
									}

									// 获取该 JSON 列在 SQL 中的原始引用（例如 table.info_column）
									var result = e.FreeParse(Expression.MakeMemberAccess(memExp.Expression, tb.Properties[trycol.CsName]));

									// 如果栈空了，说明只访问了 JSON 对象本身，没有访问内部属性
									if (parentMemExps.Any() == false) {
										e.Result = result;
										return true;
									}

									// 构建 JSON Path
									var jsonPath = "";
									switch (fsql.Ado.DataType) {
										case DataType.Sqlite:
										case DataType.MySql:
										case DataType.OdbcMySql:
										case DataType.CustomMySql:
											StyleJsonExtract(); // MySQL/SQLite 风格: json_extract(col, '$.a.b')
											return true;
										case DataType.SqlServer:
										case DataType.OdbcSqlServer:
										case DataType.CustomSqlServer:
										case DataType.Oracle:
										case DataType.OdbcOracle:
										case DataType.CustomOracle:
										case DataType.Dameng:
											StyleJsonValue(); // SQLServer/Oracle 风格: json_value(col, '$.a.b')
											return true;
										case DataType.DuckDB:
											StyleDotAccess(); // DuckDB 风格: col['a']['b']
											return true;
									}
									StylePgJson(); // PostgreSQL 风格: col->'a'->>'b'
									return true;

									// 内部辅助函数：生成不同数据库的 JSON 提取 SQL
									void StyleJsonExtract() {
										while (parentMemExps.Any()) {
											memExp = parentMemExps.Pop();
											jsonPath = $"{jsonPath}.{memExp.Member.Name}";
										}
										e.Result = $"json_extract({result},'${jsonPath}')";
									}
									void StyleJsonValue() {
										while (parentMemExps.Any()) {
											memExp = parentMemExps.Pop();
											jsonPath = $"{jsonPath}.{memExp.Member.Name}";
										}
										e.Result = $"json_value({result},'${jsonPath}')";
									}
									void StyleDotAccess() {
										while (parentMemExps.Any()) {
											memExp = parentMemExps.Pop();
											result = $"{result}['{memExp.Member.Name}']";
										}
										e.Result = result;
									}
									void StylePgJson() {
										while (parentMemExps.Any()) {
											memExp = parentMemExps.Pop();
											// PG 中 -> 返回 json 对象，->> 返回文本。最后一个节点通常用 ->> 以便进行比较
											var opt = parentMemExps.Any() ? "->" : $"->>{(memExp.Type.IsArrayOrList() ? "/*json array*/" : "")}";
											result = $"{result}{opt}'{memExp.Member.Name}'";
										}
										e.Result = result;
									}
							}
						}
					}
				};
				break;
		}
	}
}