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

public static class FreeSqlJsonMapCoreExtensions {
	private static int _IsAoped = 0;
	private static readonly ConcurrentDictionary<Type, bool> _DicTypes = new ConcurrentDictionary<Type, bool>();

	//private static readonly MethodInfo _MethodJsonConvertDeserializeObject = typeof(JsonConvert).GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type), typeof(JsonSerializerSettings) });
	//private static readonly MethodInfo _MethodJsonConvertSerializeObject = typeof(JsonConvert).GetMethod("SerializeObject", new[] { typeof(object), typeof(JsonSerializerSettings) });

	//private static readonly MethodInfo _MethodJsonConvertDeserializeObject = typeof(JsonSerializer).GetMethod("Deserialize", new[] { typeof(string), typeof(Type), typeof(JsonSerializerOptions) });

	//private static readonly MethodInfo _MethodJsonConvertSerializeObject = typeof(JsonSerializer).GetMethod("Serialize", new[] { typeof(object), typeof(Type), typeof(JsonSerializerOptions) });

	// 修改点 1: 更精确地获取 Deserialize 方法
	// public static object? Deserialize(string json, Type returnType, JsonSerializerOptions? options = null)
	private static readonly MethodInfo _MethodJsonConvertDeserializeObject = typeof(JsonSerializer).GetMethods(BindingFlags.Public | BindingFlags.Static)
		.FirstOrDefault(m => m.Name == "Deserialize" && m.IsGenericMethod == false && m.GetParameters().Length == 3 &&
			m.GetParameters()[0].ParameterType == typeof(string) &&
			m.GetParameters()[1].ParameterType == typeof(Type) &&
			m.GetParameters()[2].ParameterType == typeof(JsonSerializerOptions));

	// 修改点 2: 更精确地获取 Serialize 方法
	// public static string Serialize(object? value, Type inputType, JsonSerializerOptions? options = null)
	private static readonly MethodInfo _MethodJsonConvertSerializeObject = typeof(JsonSerializer).GetMethods(BindingFlags.Public | BindingFlags.Static)
		.FirstOrDefault(m => m.Name == "Serialize" && m.IsGenericMethod == false && m.GetParameters().Length == 3 &&
			m.GetParameters()[0].ParameterType == typeof(object) &&
			m.GetParameters()[1].ParameterType == typeof(Type) &&
			m.GetParameters()[2].ParameterType == typeof(JsonSerializerOptions));

	private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, bool>> _DicJsonMapFluentApi = new ConcurrentDictionary<Type, ConcurrentDictionary<string, bool>>();
	private static readonly object _ConcurrentObj = new object();

	public static ColumnFluent JsonMap(this ColumnFluent col) {
		_DicJsonMapFluentApi.GetOrAdd(col._entityType, et => new ConcurrentDictionary<string, bool>())
			.GetOrAdd(col._property.Name, pn => true);
		return col;
	}

	/// <summary>
	/// When the entity class property is <see cref="object"/> and the attribute is marked as <see cref="JsonMapAttribute"/>, map storage in JSON format. <br />
	/// 当实体类属性为【对象】时，并且标记特性 [JsonMap] 时，该属性将以JSON形式映射存储，默认配置，启用宽松的字符编码（防止中文被转义）和忽略大小写
	/// </summary>
	/// <returns></returns>
	public static void UseJsonMap(this IFreeSql fsql) => UseJsonMap(fsql, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });

	public static void UseJsonMap(this IFreeSql fsql, JsonSerializerOptions settings) {
		if (Interlocked.CompareExchange(ref _IsAoped, 1, 0) == 0) {
			FreeSql.Internal.Utils.GetDataReaderValueBlockExpressionSwitchTypeFullName.Add((returnTarget, valueExp, type) => {
				if (_DicTypes.ContainsKey(type)) {
					return Expression.IfThenElse(
					Expression.TypeIs(valueExp, type),
					Expression.Return(returnTarget, valueExp),
					Expression.Return(returnTarget, Expression.TypeAs(Expression.Call(_MethodJsonConvertDeserializeObject, Expression.Convert(valueExp, typeof(string)), Expression.Constant(type), Expression.Constant(settings)), type))
					);
				}

				return null;
			});
		}

		fsql.Aop.ConfigEntityProperty += (s, e) => {
			var isJsonMap = e.Property.GetCustomAttributes(typeof(JsonMapAttribute), false).Any() || (_DicJsonMapFluentApi.TryGetValue(e.EntityType, out var tryjmfu) && tryjmfu.ContainsKey(e.Property.Name));
			if (isJsonMap) {
				if (_DicTypes.ContainsKey(e.Property.PropertyType) == false &&
					FreeSql.Internal.Utils.dicExecuteArrayRowReadClassOrTuple.ContainsKey(e.Property.PropertyType)) {
					return; //基础类型使用 JsonMap 无效
				}

				if (e.ModifyResult.MapType == null) {
					switch (fsql.Ado.DataType) {
						case DataType.PostgreSQL:
							e.ModifyResult.MapType = typeof(JsonObject);
							break;
						default:
							e.ModifyResult.MapType = typeof(string);
							e.ModifyResult.StringLength = -2;
							break;
					}
				}
				if (_DicTypes.TryAdd(e.Property.PropertyType, true)) {
					lock (_ConcurrentObj) {
						FreeSql.Internal.Utils.dicExecuteArrayRowReadClassOrTuple[e.Property.PropertyType] = true;
						//FreeSql.Internal.Utils.GetDataReaderValueBlockExpressionObjectToStringIfThenElse.Add((returnTarget, valueExp, elseExp, type) => {
						//	return Expression.IfThenElse(
						//		Expression.TypeIs(valueExp, e.Property.PropertyType),
						//		Expression.Return(returnTarget, Expression.Call(_MethodJsonConvertSerializeObject, Expression.Convert(valueExp, typeof(object)), Expression.Constant(settings)), typeof(object)),
						//		elseExp);
						//});

						// 2. 处理写入 (Serialize): Object -> DB String
						// 这个钩子期望返回 string，但如果 elseExp 执行，可能返回 object
						// 所以这里的 returnTarget 类型应该是 object (因为 Expression.Return 的类型必须匹配 LabelTarget)
						FreeSql.Internal.Utils.GetDataReaderValueBlockExpressionObjectToStringIfThenElse.Add((returnTarget, valueExp, elseExp, type) => {
							// 构造 Serialize 调用表达式
							var serializeCall = Expression.Call(_MethodJsonConvertSerializeObject,
								Expression.Convert(valueExp, typeof(object)),
								Expression.Constant(e.Property.PropertyType),
								Expression.Constant(settings));

							return Expression.IfThenElse(
								Expression.TypeIs(valueExp, e.Property.PropertyType),
								// 如果类型匹配，执行序列化，并将结果(string)转换为 object 返回 (以匹配 returnTarget 的类型)
								Expression.Return(returnTarget, Expression.Convert(serializeCall, typeof(object))),
								elseExp);
						});
					}
				}
			}
		};
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
					//if (e.Expression is MethodCallExpression callExp)
					//{
					//    var objExp = callExp.Object;
					//    var objType = objExp?.Type;
					//    if (objType?.FullName == "System.Byte[]") return;

					//    if (objType == null && callExp.Method.DeclaringType == typeof(Enumerable))
					//    {
					//        objExp = callExp.Arguments.FirstOrDefault();
					//        objType = objExp?.Type;
					//    }
					//    if (objType == null) objType = callExp.Method.DeclaringType;
					//    if (objType != null || objType.IsArrayOrList())
					//    {
					//        string left = null;
					//        switch (callExp.Method.Name)
					//        {
					//            case "Any":
					//                left = objExp == null ? null : getExp(objExp);
					//                if (left.StartsWith("(") || left.EndsWith(")")) left = $"array[{left.TrimStart('(').TrimEnd(')')}]";
					//                return $"(case when {left} is null then 0 else array_length({left},1) end > 0)";
					//            case "Contains":
					//        }
					//    }
					//}
					//处理 mysql enum -> int
					switch (fsql.Ado.DataType) {
						case DataType.MySql:
						case DataType.OdbcMySql:
						case DataType.CustomMySql:
							if (e.Expression.NodeType == ExpressionType.Equal &&
								e.Expression is BinaryExpression binaryExpression) {
								var comonExp = (fsql.Select<object>() as Select0Provider)._commonExpression;
								var leftExp = binaryExpression.Left;
								var rightExp = binaryExpression.Right;
								if (
								   leftExp.NodeType == ExpressionType.Convert &&
								   leftExp is UnaryExpression leftExpUexp &&
								   leftExpUexp.Operand?.Type.NullableTypeOrThis().IsEnum == true &&

								   rightExp.NodeType == ExpressionType.Convert &&
								   rightExp is UnaryExpression rightExpUexp &&
								   rightExpUexp.Operand?.Type.NullableTypeOrThis().IsEnum == true) {
									string leftSql = null, rightSql = null;
									if (leftExpUexp.Operand.NodeType == ExpressionType.MemberAccess &&
										LocalParseMemberExp(leftExpUexp.Operand as MemberExpression)) {
										leftSql = e.Result;
									}

									if (rightExpUexp.Operand.NodeType == ExpressionType.MemberAccess &&
										LocalParseMemberExp(rightExpUexp.Operand as MemberExpression)) {
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
							if (e.Expression.NodeType == ExpressionType.Call &&
								e.Expression is MethodCallExpression callExp &&
								callExp.Method.Name == "Contains") {
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
									if (memExp.NodeType == ExpressionType.MemberAccess &&
										memExp.Type.NullableTypeOrThis().IsEnum &&
										LocalParseMemberExp(memExp as MemberExpression)) {
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

					//解析 POCO Json   a.Customer.Name
					if (e.Expression.NodeType == ExpressionType.MemberAccess) {
						LocalParseMemberExp(e.Expression as MemberExpression);
					}

					bool LocalParseMemberExp(MemberExpression memExp) {
						if (memExp == null) {
							return false;
						}

						if (e.Expression.IsParameter() == false) {
							return false;
						}

						var parentMemExps = new Stack<MemberExpression>();
						parentMemExps.Push(memExp);
						while (true) {
							switch (memExp.Expression.NodeType) {
								case ExpressionType.MemberAccess:
								case ExpressionType.Parameter:
									break;
								default:
									return false;
							}
							switch (memExp.Expression.NodeType) {
								case ExpressionType.MemberAccess:
									memExp = memExp.Expression as MemberExpression;
									if (memExp == null) {
										return false;
									}

									parentMemExps.Push(memExp);
									break;
								case ExpressionType.Parameter:
									var tb = fsql.CodeFirst.GetTableByEntity(memExp.Expression.Type);
									if (tb == null) {
										return false;
									}

									if (tb.ColumnsByCs.TryGetValue(parentMemExps.Pop().Member.Name, out var trycol) == false) {
										return false;
									}

									if (_DicTypes.ContainsKey(trycol.CsType) == false) {
										return false;
									}

									var result = e.FreeParse(Expression.MakeMemberAccess(memExp.Expression, tb.Properties[trycol.CsName]));
									if (parentMemExps.Any() == false) {
										e.Result = result;
										return true;
									}
									var jsonPath = "";
									switch (fsql.Ado.DataType) {
										case DataType.Sqlite:
										case DataType.MySql:
										case DataType.OdbcMySql:
										case DataType.CustomMySql:
											StyleJsonExtract();
											return true;
										case DataType.SqlServer:
										case DataType.OdbcSqlServer:
										case DataType.CustomSqlServer:
										case DataType.Oracle:
										case DataType.OdbcOracle:
										case DataType.CustomOracle:
										case DataType.Dameng:
											StyleJsonValue();
											return true;
										case DataType.DuckDB:
											StyleDotAccess();
											return true;
									}
									StylePgJson();
									return true;

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