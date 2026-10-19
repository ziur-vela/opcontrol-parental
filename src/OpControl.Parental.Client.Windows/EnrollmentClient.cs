using System.Net.Http.Json;
using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class EnrollmentClient
{
    private readonly HttpClient _http;
    private readonly ILogger<EnrollmentClient> _logger;

    public EnrollmentClient(HttpClient http, ILogger<EnrollmentClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<EnrollResponse> EnrollAsync(ClientOptions options, string code, CancellationToken ct)
    {
        var request = new EnrollRequest(
            code,
            string.IsNullOrWhiteSpace(options.DeviceName) ? Environment.MachineName : options.DeviceName!,
            DevicePlatform.Windows,
            Environment.OSVersion.VersionString,
            TimeZoneInfo.Local.Id,
            string.IsNullOrWhiteSpace(options.OsAccountName) ? Environment.UserName : options.OsAccountName);

        using var response = await _http.PostAsJsonAsync($"{options.ServerUrl.TrimEnd('/')}/api/enroll", request, ClientOptions.JsonOptions, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Enrollment failed ({Status}): {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Enrollment failed ({(int)response.StatusCode}). {body}");
        }

        var result = JsonSerializer.Deserialize<EnrollResponse>(body, ClientOptions.JsonOptions)
            ?? throw new InvalidOperationException("Enrollment response was empty.");
        _logger.LogInformation("Enrolled as device {DeviceId} ({DeviceName})", result.DeviceId, result.DeviceName);
        return result;
    }

    public async Task<DeviceRefreshResponse?> RefreshAsync(string serverUrl, string credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl.TrimEnd('/')}/api/device/refresh");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Credential refresh failed with status {Status}", (int)response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<DeviceRefreshResponse>(ClientOptions.JsonOptions, ct);
    }
}
