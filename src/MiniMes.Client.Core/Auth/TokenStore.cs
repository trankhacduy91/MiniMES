using MiniMes.Contracts.Auth;

namespace MiniMes.Client.Core.Auth;

// The logged-in session (token + who). Singleton: one per app.
public interface ITokenStore
{
    LoginResponse? Session { get; }
    string? AccessToken => Session?.Token;
    void Set(LoginResponse session);
    void Clear();
}

// In memory only: closing the app logs out. Kiosk shift tokens are not persisted to disk.
public sealed class TokenStore : ITokenStore
{
    public LoginResponse? Session { get; private set; }

    public void Set(LoginResponse session) => Session = session;

    public void Clear() => Session = null;
}
