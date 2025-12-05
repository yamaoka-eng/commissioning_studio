using Eclipse.eCAL.Core;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace commissioning_studio.Ecal
{
    /// <summary>
    /// ecal接口调用，特性：全局初始化、客户端缓存、线程安全、一行调用
    /// </summary>
    public class EcalCaller
    {
        // 全局 eCAL 初始化标记
        private static bool _ecalInited;
        // 客户端缓存：key=服务名，value=(客户端, 线程锁)
        private static readonly ConcurrentDictionary<string, (ServiceClient Client, ReaderWriterLockSlim Lock)> _clientCache = new();
        // 初始化锁（确保 eCAL 只初始化一次）
        private static readonly object _initLock = new();

        /// <summary>
        /// 调用 eCAL 服务接口
        /// </summary>
        /// <param name="pathtmStr">服务名/接口名（格式："服务名/文件名/接口名"，如 "TestService/file/test"）</param>
        /// <param name="params">请求参数（匿名对象/字典，自动转 JSON）</param>
        /// <param name="timeoutSec">超时时间（秒）</param>
        /// <returns>JSON 字符串（如 "{\"state\":true}"）</returns>
        public string Call(string pathtmStr, object param = null, double timeoutSec = 5.0)
        {
            var parts = pathtmStr.Split(new[] { '/' }, 2);
            var serviceName = parts[0].Trim();
            var methodName = parts[1].Trim();

            // 全局初始化 eCAL（仅执行一次）
            InitEcalOnce();

            try
            {
                // 获取/创建客户端（缓存复用）
                var (client, clientLock) = GetOrCreateClient(serviceName);

                // 参数序列化（对象 → JSON 字符串 → 字节数组）
                string requestJson = param == null ? "{}" : JsonConvert.SerializeObject(param);
                byte[] requestBytes = Encoding.UTF8.GetBytes(requestJson);

                // 线程安全调用接口
                using (new ReaderWriterLockSlimWrapper(clientLock, LockType.Read))
                {
                    // 等待客户端连接
                    if (!WaitForClientConnected(client, timeoutSec))
                    {
                        return JsonConvert.SerializeObject(new { state = false, error_msg = $"连接服务 {serviceName} 超时" });
                    }

                    // 调用ecal接口
                    List<ServiceResponse> responses = client.CallWithResponse(
                        methodName: methodName,
                        request: requestBytes,
                        timeoutMs: (int)(timeoutSec * 1000)
                    );

                    // 处理响应
                    if (responses.Count > 0 && responses[0].CallState == CallState.Executed)
                    {
                        var raw = Encoding.UTF8.GetString(responses[0].Response);
                        return raw;
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
        /// 全局初始化 eCAL（仅执行一次，线程安全）
        /// </summary>
        public static void InitEcalOnce()
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
        public static (ServiceClient Client, ReaderWriterLockSlim Lock) GetOrCreateClient(string serviceName)
        {
            return _clientCache.GetOrAdd(serviceName, key =>
            {
                ServiceMethodInformationList methodList = new ServiceMethodInformationList();
                methodList.Methods.Add(new ServiceMethodInformation("", new DataTypeInformation(), new DataTypeInformation()));
                ServiceClient client = new ServiceClient(key, methodList);
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