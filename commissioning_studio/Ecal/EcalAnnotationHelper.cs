using commissioning_studio.Ecal;
using Newtonsoft.Json;
using System;
using System.Linq;

namespace CommissioningStudio.Ecal
{
    public static class EcalAnnotationHelper
    {
        const string DefaultServiceName = "modular";
        static readonly EcalCaller _caller = new EcalCaller();

        /// <summary>
        /// 调用 eCAL API，并返回 ApiResponse<T>。
        /// 支持传入 "Tricolour_light/set_light"（会被自动转换为 "modular/Tricolour_light/set_light"）
        /// 或者直接传入完整的 "service/method"。
        /// </summary>
        public static EcalResponse<T>? CallApi<T>(string pathtmStr, object param = null, double timeoutSec = 5.0)
        {
            if (string.IsNullOrWhiteSpace(pathtmStr))
                throw new ArgumentException("pathtmStr 不能为空", nameof(pathtmStr));

            var slashCount = pathtmStr.Count(c => c == '/');
            string normalized;
            if (slashCount >= 2)
            {
                normalized = pathtmStr;
            }
            else
            {
                normalized = $"{DefaultServiceName}/{pathtmStr}";
            }

            var json = _caller.Call(normalized, param, timeoutSec);

            if (string.IsNullOrWhiteSpace(json))
                return null;

            // 先尝试反序列化成 ApiResponse<T>
            try
            {
                var apiResp = JsonConvert.DeserializeObject<EcalResponse<T>>(json);
                if (apiResp != null && (apiResp.state || apiResp.state == false))
                {
                    return apiResp;
                }
            }
            catch { /* 忽略，下面尝试直接解析为 T */ }

            // 如果不是包装结构（兼容旧返回），尝试直接解析为 T 并封装
            try
            {
                var data = JsonConvert.DeserializeObject<T>(json);
                return new EcalResponse<T> { state = true, data = data };
            }
            catch
            {
                // 最后兜底：把原始字符串放入 error_msg
                return new EcalResponse<T> { state = false, error_msg = json };
            }
        }
    }
}