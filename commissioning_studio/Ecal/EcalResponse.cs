namespace commissioning_studio.Ecal;

public class EcalResponse<T>
{
    public bool state { get; set; }
    public string? error_msg { get; set; }
    public T? data { get; set; }
}
