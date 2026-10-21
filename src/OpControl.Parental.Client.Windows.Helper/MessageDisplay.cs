using OpControl.Parental.Contracts;
using OpControl.Parental.Localization;

namespace OpControl.Parental.Helper;

internal static class MessageDisplay
{
    public static string Body(ChildMessageDto message)
    {
        if (string.IsNullOrWhiteSpace(message.Preset)) return message.Text;
        var key = message.Preset switch
        {
            "dinner" => "Dinner",
            "wrap-up" => "WrapUp",
            "come-find-me" => "ComeFindMe",
            _ => null
        };
        if (key is null) return message.Text;
        var localized = Loc.T("Client.Messages.Preset." + key);
        return localized.StartsWith("Client.Messages.Preset.", StringComparison.Ordinal) ? message.Text : localized;
    }

    public static string Sender(ChildMessageDto message) =>
        string.IsNullOrWhiteSpace(message.SenderName) ? Loc.T("Client.Messages.ParentFallback") : message.SenderName;

    public static string Time(ChildMessageDto message) => Time(message.CreatedUtc);

    public static string Time(DateTime utc) =>
        utc.ToLocalTime().ToString("d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}
