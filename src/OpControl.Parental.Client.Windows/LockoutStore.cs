using System.Text.Json;
using OpControl.Parental.Contracts;

namespace OpControl.Parental.Client;

public sealed class LockoutStore
{
    private sealed class Data
    {
        public int FailedAttempts { get; set; }
        public DateTime? LockoutUntilUtc { get; set; }
    }

    private readonly object _gate = new();
    private Data _data = new();

    public LockoutStore() => Load();

    public (bool LockedOut, int RemainingSeconds, int AttemptsRemaining) Check()
    {
        lock (_gate)
        {
            if (_data.LockoutUntilUtc is { } until && until > DateTime.UtcNow)
                return (true, (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds), 0);
            return (false, 0, Math.Max(0, 5 - _data.FailedAttempts));
        }
    }

    public (bool LockedOut, int RemainingSeconds, int AttemptsRemaining) RegisterFailure()
    {
        lock (_gate)
        {
            _data.FailedAttempts++;
            if (_data.FailedAttempts >= 5)
            {
                _data.FailedAttempts = 0;
                _data.LockoutUntilUtc = DateTime.UtcNow.AddSeconds(60);
            }
            Save();
            return Check();
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _data = new Data();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(ClientPaths.LockoutFile))
                _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(ClientPaths.LockoutFile)) ?? new Data();
        }
        catch { _data = new Data(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(ClientPaths.PrivateDirectory);
            File.WriteAllText(ClientPaths.LockoutFile, JsonSerializer.Serialize(_data));
        }
        catch { }
    }
}
