using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Eclipse.eCAL.Core;

namespace commissioning_studio.Ecal
{
    /// <summary>
    /// 复刻 Python RheaCaller 逻辑（基于 eCAL 官方 C# API）
    /// 特性：全局初始化、客户端缓存、线程安全、一行调用
    /// </summary>
    public class EcalCaller
    {
        // 对应 Python 的 global ECAL_INITED（全局 eCAL 初始化标记）
        private static bool _ecalInited;
        // 对应 Python 的 global CLIENT_CACHE（客户端缓存：key=服务名，value=(客户端, 线程锁)）
        private static readonly ConcurrentDictionary<string, (ServiceClient Client, ReaderWriterLockSlim Lock)> _clientCache = new();
        // 初始化锁（确保 eCAL 只初始化一次）
        private static readonly object _initLock = new();

        /// <summary>
        /// 调用 eCAL 服务接口（对应 Python 的 __call__ 方法）
        /// </summary>
        /// <param name="pathtmStr">服务名/接口名（格式："服务名/接口名"，如 "TestService/test"）</param>
        /// <param name="params">请求参数（匿名对象/字典，自动转 JSON）</param>
        /// <param name="timeoutSec">超时时间（秒）</param>
        /// <returns>服务端返回的 JSON 字符串（如 "{\"state\":true}"）</returns>
        public string Call(string pathtmStr, object param = null, double timeoutSec = 5.0)
        {
            // 1. 解析 pathtmStr → 服务名 + 接口名（对应 Python 的 PathTM 解析）
            if (!ParsePathtmStr(pathtmStr, out string serviceName, out string methodName))
            {
                return JsonConvert.SerializeObject(new { state = false, error_msg = "pathtmStr 格式错误（需为 '服务名/接口名'）" });
            }

            // 2. 全局初始化 eCAL（仅执行一次，对应 Python 的 ecal_core.initialize）
            InitEcalOnce();

            try
            {
                // 3. 获取/创建客户端（缓存复用，对应 Python 的 CLIENT_CACHE）
                var (client, clientLock) = GetOrCreateClient(serviceName);

                // 4. 参数序列化（对象 → JSON 字符串 → 字节数组，对应 Python 的 setattr(c.param, k, v)）
                string requestJson = param == null ? "{}" : JsonConvert.SerializeObject(param);
                byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);

                // 5. 线程安全调用接口（对应 Python 的 client.lock）
                using (new ReaderWriterLockSlimWrapper(clientLock, LockType.Read))
                {
                    // 等待客户端连接
                    if (!WaitForClientConnected(client, timeoutSec))
                    {
                        return JsonConvert.SerializeObject(new { state = false, error_msg = $"连接服务 {serviceName} 超时" });
                    }

                    // 调用接口（官方 CallWithResponse，对应 Python 的 c.call）
                    List<ServiceResponse> responses = client.CallWithResponse(
                        methodName: methodName,
                        request: requestBytes,
                        timeoutMs: (int)(timeoutSec * 1000)
                    );

                    // 处理响应
                    if (responses.Count > 0 && responses[0].CallState == CallState.Executed)
                    {
                        var raw = Encoding.UTF8.GetString(responses[0].Response);
                        // 回退：兼容服务端返回的伪格式（例如 {state=true,error_msg=null,data=null}）
                        return NormalizePseudoObject(raw);
                    }
                    else
                    {
                        string errorMsg = responses.Count > 0 ? responses[0].ErrorMessage : "接口调用无响应";
                        return JsonConvert.SerializeObject(new { state = false, error_msg = errorMsg });
                    }
                }
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new { state = false, error_msg = $"调用异常：{ex.Message}" });
            }
        }

        /// <summary>
        /// 简单把伪格式对象字符串转换为合法 JSON，作为容错回退（尽量不要长期依赖）
        /// 支持样例输入： "{state=true,error_msg=null,data=null}"
        /// 输出： "{\"state\":true,\"error_msg\":null,\"data\":null}"
        /// </summary>
        private string NormalizePseudoObject(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            var t = s.Trim();
            // 不是大括号或不含等号则认为已为合法 JSON
            if (!t.StartsWith("{") || !t.EndsWith("}") || !t.Contains("=")) return s;

            try
            {
                var inner = t.Substring(1, t.Length - 2);
                var parts = inner.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(p => p.Trim());
                var kvs = new List<string>();
                foreach (var p in parts)
                {
                    var idx = p.IndexOf('=');
                    if (idx < 0) continue;
                    var key = p.Substring(0, idx).Trim();
                    var val = p.Substring(idx + 1).Trim();

                    var lower = val.ToLowerInvariant();
                    // 如果是布尔或 null 或已经是对象/数组/字符串，则直接使用
                    if (lower == "true" || lower == "false" || lower == "null" || val.StartsWith("{") || val.StartsWith("[") || val.StartsWith("\""))
                    {
                        kvs.Add($"\"{key}\":{val}");
                    }
                    else
                    {
                        // 把未加引号的字符串值加上双引号并转义
                        var escaped = JsonConvert.ToString(val);
                        kvs.Add($"\"{key}\":{escaped}");
                    }
                }

                return "{" + string.Join(",", kvs) + "}";
            }
            catch
            {
                return s;
            }
        }

        /// <summary>
        /// 解析 pathtmStr 为 服务名 + 接口名（复刻 Python 的 PathTM 逻辑）
        /// </summary>
        private bool ParsePathtmStr(string pathtmStr, out string serviceName, out string methodName)
        {
            serviceName = null;
            methodName = null;

            if (string.IsNullOrEmpty(pathtmStr) || !pathtmStr.Contains("/"))
                return false;

            var parts = pathtmStr.Split(new[] { '/' }, 2);
            serviceName = parts[0].Trim();
            methodName = parts[1].Trim();

            return !string.IsNullOrEmpty(serviceName) && !string.IsNullOrEmpty(methodName);
        }

        /// <summary>
        /// 全局初始化 eCAL（仅执行一次，线程安全）
        /// </summary>
        private void InitEcalOnce()
        {
            if (_ecalInited) return;

            lock (_initLock)
            {
                if (!_ecalInited)
                {
                    // 对应 Python 的 ecal_core.initialize([], f"py_client_{os.getpid()}")
                    string clientName = $"cs_client_{Environment.ProcessId}";
                    Core.Initialize(clientName);
                    _ecalInited = true;
                    Console.WriteLine($"eCAL 初始化成功（客户端名称：{clientName}）");
                }
            }
        }

        /// <summary>
        /// 获取或创建 eCAL 客户端（缓存复用，线程安全）
        /// </summary>
        private (ServiceClient Client, ReaderWriterLockSlim Lock) GetOrCreateClient(string serviceName)
        {
            return _clientCache.GetOrAdd(serviceName, key =>
            {
                // 对应 Python 的 CLIENT_CACHE[pathtm.path] = ecal_service.Client(pathtm.path)
                ServiceMethodInformationList methodList = new ServiceMethodInformationList();
                methodList.Methods.Add(new ServiceMethodInformation("", new DataTypeInformation(), new DataTypeInformation()));
                ServiceClient client = new ServiceClient(key, methodList);

                // 对应 Python 的 client.lock = threading.Lock()
                var clientLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);

                Console.WriteLine($"创建 eCAL 客户端（服务名：{key}）");
                return (client, clientLock);
            });
        }

        /// <summary>
        /// 等待客户端连接服务端
        /// </summary>
        private bool WaitForClientConnected(ServiceClient client, double timeoutSec)
        {
            DateTime start = DateTime.Now;
            while ((DateTime.Now - start).TotalSeconds < timeoutSec)
            {
                if (client.IsConnected())
                    return true;
                Thread.Sleep(100);
            }
            return false;
        }

        /// <summary>
        /// ReaderWriterLockSlim 包装类（自动释放锁）
        /// </summary>
        private enum LockType { Read, Write }
        private class ReaderWriterLockSlimWrapper : IDisposable
        {
            private readonly ReaderWriterLockSlim _lock;
            private readonly LockType _lockType;

            public ReaderWriterLockSlimWrapper(ReaderWriterLockSlim lockObj, LockType lockType)
            {
                _lock = lockObj;
                _lockType = lockType;

                if (_lockType == LockType.Read)
                    _lock.EnterReadLock();
                else
                    _lock.EnterWriteLock();
            }

            public void Dispose()
            {
                if (_lockType == LockType.Read)
                    _lock.ExitReadLock();
                else
                    _lock.ExitWriteLock();
            }
        }
    }
}