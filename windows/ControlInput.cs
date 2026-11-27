namespace RemoteControl;

internal sealed class ControlInput
{
    public string? Action { get; set; }
    public double? X { get; set; }
    public double? Y { get; set; }
    public int? Delta { get; set; }
    public string? Text { get; set; }
    public string? Key { get; set; }
    public int? VirtualKey { get; set; }
    public bool Extended { get; set; }
}
