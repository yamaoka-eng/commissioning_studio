namespace commissioning_studio.Modules;

using commissioning_studio.Ecal;

public class Temperature_humidity
{
    // 小 DTO，匹配服务端返回的 { temperature, humidity }
    public class TemperatureHumidityDto
    {
        public double temperature { get; set; }
        public double humidity { get; set; }
    }

    [ModularOp]
    public async Task<object> get_temperature_humidity(string type)
    {
        if (type == "a")
        {
            return new EcalResponse<TemperatureHumidityDto>
            {
                state = true,
                data = new TemperatureHumidityDto { temperature = 23.5, humidity = 45.2 }
            };
        }
        else
        {
            return new EcalResponse<TemperatureHumidityDto>
            {
                state = true,
                data = new TemperatureHumidityDto { temperature = 100, humidity = 100 }
            };
        }
        
    }

    [ModularOp]
    public async Task<object> get_test()
    {
        return new EcalResponse<TemperatureHumidityDto>
        {
            state = true,
            data = new TemperatureHumidityDto { temperature = 2, humidity = 1 }
        };
    }
}
