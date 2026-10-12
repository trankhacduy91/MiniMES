using System.Net;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels;
using MiniMes.Contracts.Auth;

namespace MiniMes.UnitTests.Client;

// MVVM pay-off: the login screens are tested without opening a window.
public class LoginViewModelTests
{
    private sealed class FakeAuth(Exception? fail = null) : IAuthClient
    {
        public (string Code, string Secret)? LastCall { get; private set; }

        public Task<LoginResponse> LoginAsync(string employeeCode, string password, CancellationToken ct = default)
            => Respond(employeeCode, password);

        public Task<LoginResponse> LoginOperatorAsync(string employeeCode, string stationCode, CancellationToken ct = default)
            => Respond(employeeCode, stationCode);

        public void Logout() { }

        private Task<LoginResponse> Respond(string code, string secret)
        {
            LastCall = (code, secret);
            return fail is null
                ? Task.FromResult(new LoginResponse("t", DateTime.UtcNow, code, "Name", ["Admin"], null))
                : Task.FromException<LoginResponse>(fail);
        }
    }

    private sealed class FakeNavigation : INavigation
    {
        public int HomeShown { get; private set; }
        public void ShowLogin() { }
        public void ShowHome() => HomeShown++;
    }

    private static IOptions<ClientOptions> Station(string? code)
        => Options.Create(new ClientOptions { ApiBaseUrl = "http://x/", StationCode = code });

    // ---------- BackOffice ----------

    [Theory]
    [InlineData("", "")]
    [InlineData("admin", "")]
    [InlineData("", "Demo@12345")]
    [InlineData("   ", "Demo@12345")]
    public void Login_is_disabled_until_code_and_password_are_filled(string code, string password)
    {
        var vm = new UserLoginViewModel(new FakeAuth(), new FakeNavigation()) { EmployeeCode = code, Password = password };

        Assert.False(vm.LoginCommand.CanExecute(null));
    }

    [Fact]
    public async Task Successful_login_goes_home_and_forgets_the_password()
    {
        var auth = new FakeAuth();
        var nav = new FakeNavigation();
        var vm = new UserLoginViewModel(auth, nav) { EmployeeCode = " admin ", Password = "Demo@12345" };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(("admin", "Demo@12345"), auth.LastCall); // code trimmed
        Assert.Equal(1, nav.HomeShown);
        Assert.Equal(string.Empty, vm.Password);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }

    [Fact]
    public async Task Wrong_password_shows_the_resx_message_not_the_server_text()
    {
        var nav = new FakeNavigation();
        var vm = new UserLoginViewModel(new FakeAuth(new ApiException(HttpStatusCode.Unauthorized, "server text")), nav)
        {
            EmployeeCode = "admin",
            Password = "wrong",
        };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Login_InvalidCredentials, vm.ErrorMessage);
        Assert.Equal(0, nav.HomeShown);
    }

    [Fact]
    public async Task Api_down_shows_cannot_reach_server()
    {
        var vm = new UserLoginViewModel(new FakeAuth(new ApiException(null, Strings.Error_CannotReachServer)), new FakeNavigation())
        {
            EmployeeCode = "admin",
            Password = "Demo@12345",
        };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Error_CannotReachServer, vm.ErrorMessage);
    }

    // ---------- Kiosk ----------

    [Fact]
    public async Task Badge_scan_logs_in_at_the_configured_station()
    {
        var auth = new FakeAuth();
        var nav = new FakeNavigation();
        var vm = new OperatorLoginViewModel(auth, nav, Station("L1-TEST-01")) { EmployeeCode = "EMP00123" };

        await vm.LoginCommand.ExecuteAsync(null);

        Assert.Equal(("EMP00123", "L1-TEST-01"), auth.LastCall);
        Assert.Equal(1, nav.HomeShown);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Failed_scan_shows_message_and_clears_the_box_for_the_next_scan(HttpStatusCode status)
    {
        var vm = new OperatorLoginViewModel(new FakeAuth(new ApiException(status, "server text")), new FakeNavigation(), Station("L1-TEST-01"))
        {
            EmployeeCode = "EMP99999",
        };

        await vm.LoginCommand.ExecuteAsync(null);

        var expected = status == HttpStatusCode.Unauthorized
            ? Strings.Kiosk_UnknownBadge
            : string.Format(Strings.Kiosk_StationNotFound, "L1-TEST-01");
        Assert.Equal(expected, vm.ErrorMessage);
        Assert.Equal(string.Empty, vm.EmployeeCode);
    }

    [Fact]
    public void Kiosk_without_station_code_cannot_log_in()
    {
        var vm = new OperatorLoginViewModel(new FakeAuth(), new FakeNavigation(), Station(null)) { EmployeeCode = "EMP00123" };

        Assert.False(vm.LoginCommand.CanExecute(null));
    }

    [Fact]
    public void Resx_strings_are_generated_and_readable()
        => Assert.Equal("Login", Strings.Login_Button);
}
