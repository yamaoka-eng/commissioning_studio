public class Temperature_humidity
    {
        [ModularOp]
        public async Task<object> get_temperature_humidity()
        {
            await Task.Delay(10);
            return new { temperature = 23.5, humidity = 45.2 };
        }
    }