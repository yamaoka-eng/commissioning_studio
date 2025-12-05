using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace commissioning_studio.Ecal
{
    public static class EcalAnnotationHelper
    {   
        // ecal服务器名称
        const string DefaultServiceName = "modulars";
        static readonly EcalCaller _caller = new EcalCaller();

        /// <summary>
        /// 调用 eCAL API 并返回 EcalResponse<T> 类型的结果
        /// 支持异步调用，超时时间默认 5 秒
        /// 若 data 字段为 JSON 字符串，会自动解析为 T 类型
        /// </summary>
        public static EcalResponse<T>? CallApi<T>(string pathtmStr, object param = null, double timeoutSec = 5.0)
        {

            string normalized = $"{DefaultServiceName}/{pathtmStr}";
            // 调用 eCAL API 并获取 JSON 字符串结果
            var json = _caller.Call(normalized, param, timeoutSec);
            // 解析 JSON 字符串为 EcalResponse<T> 类型
            try
            {
                return JsonConvert.DeserializeObject<EcalResponse<T>>(json.Trim());
            }
            catch
            {
                // 解析失败，返回原始 JSON 字符串作为错误信息
                return new EcalResponse<T> { state = false, error_msg = json };
            }
        }
    }
}