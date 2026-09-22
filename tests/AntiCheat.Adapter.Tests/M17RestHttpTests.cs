using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using HttpServer;
using NUnit.Framework;
using Rests;
using Native = TShockAPI.TShock;

namespace AntiCheat.Adapter.Tests;

[TestFixture, NonParallelizable]
public sealed class M17RestHttpTests
{
    [Test]
    public async Task OwnedRealHttpNativeTokenPermissionHashDatabaseRejectionAndNaturalRecovery()
    {
        using var f = new M17RestWorkTests.Fixture(TimeProvider.System);
        var listener = HttpServer.HttpListener.Create(IPAddress.Loopback, 0);
        var nativeRequest = (EventHandler<RequestEventArgs>)typeof(Rest).GetMethod("OnRequest", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(EventHandler<RequestEventArgs>), f.Api);
        listener.RequestReceived += nativeRequest;
        string status = "failed"; int port = 0, requestCount = 0; bool stopped = false; string? token = null;
        Socket? capturedListenerSocket = null;
        var elapsed = Stopwatch.StartNew(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.ConnectionClose = true;
        try
        {
            listener.Start(4);
            // Port zero is allocated by the OS; inspect only this newly owned listener.
            var socket = Fields(listener.GetType()).Select(field => field.GetValue(listener)).OfType<TcpListener>().Single();
            capturedListenerSocket = socket.Server;
            var endpoint = (IPEndPoint)socket.LocalEndpoint!;
            Assert.That(IPAddress.IsLoopback(endpoint.Address), Is.True); port = endpoint.Port;
            Assert.That(port, Is.GreaterThan(0)); http.BaseAddress = new Uri("http://127.0.0.1:" + port);
            var login = await Call("/v2/token/create", [("username", "RestAdmin"), ("password", M17RestWorkTests.Fixture.Password)]);
            Assert.That(login.Status, Is.EqualTo(HttpStatusCode.OK)); token = login.Json.GetProperty("token").GetString()!;
            var readerLogin = await Call("/v2/token/create", [("username", "RestReader"), ("password", M17RestWorkTests.Fixture.Password)]);
            string readerToken = readerLogin.Json.GetProperty("token").GetString()!;
            Assert.That(f.TokenVerifies, Is.EqualTo(2));
            Assert.That((await Call("/v2/users/create", [("user", "HttpTarget"), ("password", M17RestWorkTests.Fixture.Password)])).Status, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That((await Call("/v2/users/create", [("token", readerToken), ("user", "HttpTarget"), ("password", M17RestWorkTests.Fixture.Password)])).Status, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(f.Hashes, Is.Zero); Assert.That(f.Guard.Admitted, Is.Zero);
            Assert.That((await Call("/v2/users/create", [("token", token), ("user", "HttpTarget"), ("password", M17RestWorkTests.Fixture.Password)])).Status, Is.EqualTo(HttpStatusCode.OK));
            for (int i = 0; i < 7; i++)
                Assert.That((await Update()).Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(f.Hashes, Is.EqualTo(8)); Assert.That(f.Guard.Admitted, Is.EqualTo(8));
            string before = Native.UserAccounts.GetUserAccountByName("HttpTarget").Password;
            Assert.That((await Update()).Status, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(f.Hashes, Is.EqualTo(8)); Assert.That(f.Guard.Blocked, Is.EqualTo(1));
            Assert.That(Native.UserAccounts.GetUserAccountByName("HttpTarget").Password == before, Is.True);
            Assert.That((await Call("/tokentest", [("token", readerToken)])).Status, Is.EqualTo(HttpStatusCode.OK), "Same-NAT unrelated authorized REST endpoint remains available.");
            await Task.Delay(TimeSpan.FromSeconds(5.2), deadline.Token);
            Assert.That((await Update()).Status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(f.Hashes, Is.EqualTo(9)); Assert.That(f.Guard.Admitted, Is.EqualTo(9));
            Assert.That(Native.UserAccounts.GetUserAccountByName("HttpTarget").Password != before, Is.True);
            Assert.That(f.Guard.Healthy, Is.True); f.CheckCompletedLog(); status = "passed";

            Task<(HttpStatusCode Status, JsonElement Json)> Update() => Call("/v2/users/update",
                [("token", token!), ("user", "HttpTarget"), ("type", "name"), ("password", M17RestWorkTests.Fixture.Password)]);
        }
        finally
        {
            listener.RequestReceived -= nativeRequest; listener.Stop();
            // This native library leaves IsStarted=true after Stop. Inspect the captured
            // original handle and prove the exact owned endpoint can be bound again.
            bool endpointReleased = false;
            if (port > 0)
            {
                var releasedEndpointProbe = new TcpListener(IPAddress.Loopback, port);
                try { releasedEndpointProbe.Start(1); endpointReleased = true; }
                finally { releasedEndpointProbe.Stop(); }
            }
            stopped = capturedListenerSocket?.SafeHandle.IsClosed == true && endpointReleased;
            status = status == "passed" && stopped ? "passed" : "failed";
            await File.WriteAllTextAsync(Path.Combine(f.DirectoryPath, "http-summary.json"), JsonSerializer.Serialize(new
            {
                status, listenerAddress = "127.0.0.1", port, requestCount, elapsedMs = elapsed.Elapsed.TotalMilliseconds,
                stopped, endpointReleased, nativeIsStartedAfterStop = listener.IsStarted,
                capturedHandleClosed = capturedListenerSocket?.SafeHandle.IsClosed,
                hashes = f.Hashes, verifies = f.TokenVerifies, admitted = f.Guard.Admitted, blocked = f.Guard.Blocked,
                gameServerStarted = false, nativeHttpParserAndRoute = true, actualTokenAndPermission = true,
                credentialsRecorded = false, gameplayAccountOrSessionSynthesized = false,
                httpServerLoadedSha256 = f.Guard.VerifiedHttpServerSha256,
                httpServerLoadedLocation = typeof(IHttpContext).Assembly.Location
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.That(stopped, Is.True);
        TestContext.WriteLine("Owned native HttpServer real HTTP scenario passed; requests=" + requestCount + "; listener stopped=" + stopped);
        async Task<(HttpStatusCode Status, JsonElement Json)> Call(string route, (string Key, string Value)[] fields)
        {
            string query = string.Join("&", fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
            requestCount++;
            // Never persist request URIs, response bodies, passwords or tokens on assertion failure.
            HttpResponseMessage response;
            try { response = await http.GetAsync(route + "?" + query, deadline.Token); }
            catch (Exception error) { throw new IOException("Owned REST HTTP request failed: " + error.GetType().Name); }
            using (response)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return (response.StatusCode, json.RootElement.Clone());
            }
        }
    }
    private static IEnumerable<FieldInfo> Fields(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
            foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)) yield return field;
    }
}

