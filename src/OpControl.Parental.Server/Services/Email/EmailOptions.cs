namespace OpControl.Parental.Server.Services;

public sealed class EmailOptions
{
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public string? ReplyTo { get; set; }
    public string? LogoUrl { get; set; }
    public bool Staging { get; set; }
}
