using Eclipse.eCAL.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace commissioning_studio.Ecal
{
    /// <summary>
    /// 标记方法为 Modular OP（类似 Python 的 @MODULAR.op.motion）。
    /// 在应用启动后，EcalService 会扫描带有该属性的方法并注册到 eCAL 服务中。
    /// 服务方法路径默认格式为: "{DeclaringTypeName}/{MethodName}"，例如 "Temperature_humidity/get_temperature_humidity"。
    /// 注意：运行时无法可靠获取源代码文件夹，建议将类名与文件夹名一一对应以保持路径一致性。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class ModularOpAttribute : Attribute
    {
        public string Group { get; }
        public ModularOpAttribute(string group = "motion") => Group = group;
    }

    /// <summary>
    /// EcalService：在 ASP.NET / Blazor 中启动 eCAL 服务并反射调用带有 ModularOpAttribute 的方法。
    /// - 调用约定：
    ///   请求为 UTF8 JSON 序列化数据，可以是:
    ///     1) 对象：{ "paramName1": value1, "paramName2": value2, ... } （按参数名绑定）
    ///     2) 数组：[ value1, value2, ... ] （按位置绑定）
    ///   返回值会被 JSON 序列化为 UTF8 byte[]。
    /// - 支持方法返回值:
    ///     - sync value (任何可序列化对象)
    ///     - Task (async void-like) -> 返回 null
    ///     - Task<T> -> 返回 T 的 JSON
    /// </summary>
    public sealed class EcalService : IDisposable
    {
        readonly string _serviceName;
        ServiceServer? _server;
        readonly ConcurrentDictionary<string, (MethodInfo Method, object? Instance)> _methodMap = new();
        bool _initialized = false;
        readonly object _lock = new();

        public EcalService(string serviceName = "modular")
        {
            _serviceName = serviceName ?? "modular";
        }

        /// <summary>
        /// 启动 eCAL、创建 ServiceServer 并注册所有带 ModularOpAttribute 的方法。
        /// 可在 ASP.NET 应用启动（例如 IHost）时调用。
        /// </summary>
        public void Start()
        {
            lock (_lock)
            {
                if (_initialized) return;

                Core.Initialize(_serviceName);
                _server = new ServiceServer(_serviceName);

                RegisterModularOpsFromLoadedAssemblies();

                // 使用单一回调处理所有方法
                _server.SetMethodCallback(new ServiceMethodInformation { MethodName = string.Empty }, OnAnyMethodCalled);

                _initialized = true;
            }
        }

        /// <summary>
        /// 停止服务并清理
        /// </summary>
        public void Stop()
        {
            lock (_lock)
            {
                if (!_initialized) return;

                _server?.Dispose();
                _server = null;
                Core.Terminate();
                _initialized = false;
                _methodMap.Clear();
            }
        }

        /// <summary>
        /// 扫描当前 AppDomain 中的已加载程序集，注册所有带有 ModularOpAttribute 的方法。
        /// </summary>
        void RegisterModularOpsFromLoadedAssemblies()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types ?? Array.Empty<Type>();
                }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    {
                        var attr = method.GetCustomAttribute<ModularOpAttribute>();
                        if (attr == null) continue;

                        var folder = method.DeclaringType?.Name ?? "global";
                        var path = $"{folder}/{method.Name}";

                        object? instance = null;
                        if (!method.IsStatic)
                        {
                            try
                            {
                                instance = Activator.CreateInstance(method.DeclaringType!);
                            }
                            catch
                            {
                                instance = null;
                            }
                        }

                        _methodMap[path] = (method, instance);
                        var methodInfo = new ServiceMethodInformation { MethodName = path };
                        _server?.SetMethodCallback(methodInfo, OnAnyMethodCalled);
                    }
                }
            }
        }

        /// <summary>
        /// 通用回调：根据 methodInfo.MethodName 查找目标方法并通过反射调用。
        /// 请求为 UTF8 JSON；响应为 UTF8 JSON。
        /// </summary>
        byte[] OnAnyMethodCalled(ServiceMethodInformation methodInfo, byte[] request)
        {
            try
            {
                var methodKey = methodInfo.MethodName;
                if (!_methodMap.TryGetValue(methodKey, out var tuple))
                {
                    var fail = new EcalResponse<object> { state = false, error_msg = $"Method '{methodKey}' not registered." };
                    return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(fail));
                }

                var method = tuple.Method;
                var instance = tuple.Instance;

                // 解析请求 JSON
                JToken? payload = null;
                if (request != null && request.Length > 0)
                {
                    var json = Encoding.UTF8.GetString(request);
                    if (!string.IsNullOrWhiteSpace(json))
                        payload = JsonConvert.DeserializeObject<JToken>(json);
                }

                var parameters = method.GetParameters();
                object?[] invokeArgs = new object?[parameters.Length];

                if (payload != null)
                {
                    if (payload.Type == JTokenType.Array)
                    {
                        // 位置参数绑定
                        var arr = (JArray)payload;
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            if (i < arr.Count)
                                invokeArgs[i] = arr[i].ToObject(parameters[i].ParameterType);
                            else if (parameters[i].HasDefaultValue)
                                invokeArgs[i] = parameters[i].DefaultValue;
                            else
                                invokeArgs[i] = GetDefault(parameters[i].ParameterType);
                        }
                    }
                    else if (payload.Type == JTokenType.Object)
                    {
                        // 命名参数绑定
                        var obj = (JObject)payload;
                        for (int i = 0; i < parameters.Length; i++)
                        {
                            var p = parameters[i];
                            if (obj.TryGetValue(p.Name!, StringComparison.OrdinalIgnoreCase, out var token))
                            {
                                invokeArgs[i] = token.ToObject(p.ParameterType);
                            }
                            else if (p.HasDefaultValue)
                            {
                                invokeArgs[i] = p.DefaultValue;
                            }
                            else
                            {
                                invokeArgs[i] = GetDefault(p.ParameterType);
                            }
                        }
                    }
                    else
                    {
                        // 单值参数且方法只接受一个参数
                        if (parameters.Length == 1)
                        {
                            invokeArgs[0] = payload.ToObject(parameters[0].ParameterType);
                        }
                        else
                        {
                            for (int i = 0; i < parameters.Length; i++)
                                invokeArgs[i] = GetDefault(parameters[i].ParameterType);
                        }
                    }
                }
                else
                {
                    // 无请求体，使用默认值或 null
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var p = parameters[i];
                        if (p.HasDefaultValue) invokeArgs[i] = p.DefaultValue;
                        else invokeArgs[i] = GetDefault(p.ParameterType);
                    }
                }

                // 调用目标方法并统一包装响应
                object? resultObj = null;
                var returnObj = method.Invoke(instance, invokeArgs);
                if (returnObj is Task task)
                {
                    task.GetAwaiter().GetResult();
                    resultObj = task.GetType().GetProperty("Result").GetValue(task);
                }
                
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(resultObj));
            }
            catch (TargetInvocationException tie)
            {
                var ex = tie.InnerException ?? tie;
                var err = new EcalResponse<object> { state = false, error_msg = ex.Message };
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(err));
            }
            catch (Exception ex)
            {
                var err = new EcalResponse<object> { state = false, error_msg = ex.Message };
                return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(err));
            }
        }

        static object? GetDefault(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

        public void Dispose()
        {
            Stop();
        }
    }
}

