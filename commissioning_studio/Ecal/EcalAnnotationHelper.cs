using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace commissioning_studio.Ecal
{
    public static class EcalAnnotationHelper
    {
        const string DefaultServiceName = "modular";
        static readonly EcalCaller _caller = new EcalCaller();

        /// <summary>
        /// 调用 eCAL API 并返回 EcalResponse<T>
        /// 支持处理服务端返回双重 JSON 编码（例如 data 字段为 JSON 字符串）的情况
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

            // 处理可能的双重编码：外层为字符串的情况（例如 "\"{...}\""）
            string rawJson = json.Trim();
            if ((rawJson.StartsWith("\"") && rawJson.EndsWith("\"")) || (rawJson.StartsWith("'") && rawJson.EndsWith("'")))
            {
                try
                {
                    rawJson = JsonConvert.DeserializeObject<string>(rawJson) ?? rawJson;
                }
                catch
                {
                    // 如无法反序列化成 string，保持原样
                }
            }

            // 优先尝试把返回值解析成 EcalResponse<T>
            try
            {
                var apiResp = JsonConvert.DeserializeObject<EcalResponse<T>>(rawJson);
                if (apiResp != null)
                {
                    // 如果解析出外层 EcalResponse 且 data 为 null，但原始 JSON 的 data 字段是一个 JSON 字符串或对象，
                    // 则进一步尝试解析内部 JSON（兼容服务端把 data 作为字符串返回的情况）
                    if (apiResp.data == null)
                    {
                        try
                        {
                            var root = JObject.Parse(rawJson);
                            var dataToken = root["data"];
                            if (dataToken != null)
                            {
                                if (dataToken.Type == JTokenType.String)
                                {
                                    var inner = dataToken.Value<string>();
                                    if (!string.IsNullOrWhiteSpace(inner))
                                    {
                                        // inner 可能是 EcalResponse<T> 或直接是 T
                                        try
                                        {
                                            var innerApi = JsonConvert.DeserializeObject<EcalResponse<T>>(inner);
                                            if (innerApi != null)
                                                return innerApi;
                                        }
                                        catch { /* ignore */ }

                                        try
                                        {
                                            var innerData = JsonConvert.DeserializeObject<T>(inner);
                                            return new EcalResponse<T> { state = apiResp.state, data = innerData };
                                        }
                                        catch { /* ignore */ }
                                    }
                                }
                                else if (dataToken.Type == JTokenType.Object || dataToken.Type == JTokenType.Array)
                                {
                                    try
                                    {
                                        var innerData = dataToken.ToObject<T>();
                                        apiResp.data = innerData;
                                        return apiResp;
                                    }
                                    catch { /* ignore */ }
                                }
                            }
                        }
                        catch { /* ignore parsing root */ }
                    }

                    return apiResp;
                }
            }
            catch
            {
                // 忽略，后续尝试解析为 T
            }

            // 如果不是标准的 EcalResponse<T>，尝试直接解析为 T（原先逻辑的一部分）
            try
            {
                var data = JsonConvert.DeserializeObject<T>(rawJson);
                return new EcalResponse<T> { state = true, data = data };
            }
            catch
            {
                // 解析失败，返回包含原始响应的错误信息
                return new EcalResponse<T> { state = false, error_msg = json };
            }
        }
    }
}