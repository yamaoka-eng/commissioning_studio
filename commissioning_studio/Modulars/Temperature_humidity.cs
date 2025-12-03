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
    public async Task<object> get_temperature_humidity()
    {
        await Task.Delay(10);
        return new EcalResponse<TemperatureHumidityDto>
        {
            state = false,
            data = new TemperatureHumidityDto { temperature = 23.5, humidity = 45.2 }
        };
    }
}
