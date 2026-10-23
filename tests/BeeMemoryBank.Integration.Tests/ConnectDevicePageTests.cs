extern alias WebApp;

using System.Net;
using System.Text;
using ApiClient = WebApp::BeeMemoryBank.Web.Services.ApiClient;
using ConnectDeviceModel = WebApp::BeeMemoryBank.Web.Pages.Admin.ConnectDeviceModel;
using NodeLanClient = WebApp::BeeMemoryBank.Web.Services.NodeLanClient;

namespace BeeMemoryBank.Integration.Tests;

/// <summary>
/// "Connect a device" on its Admin page (release 2.7.2: the page <c>/Connect</c> is folded in). Opening the page only asks the node for its status; the door to the
/// network is opened by the button, a POST, and nothing else. The node is a stand-in that writes down what it was asked.
/// </summary>
public sealed class ConnectDevicePageTests : IDisposable
{
    private const string Pin = "q7m1Xo2W3x0b7mB8pSxYl0Fh3y1rJk3n9gW2hQx1r2A";
    private const string Token = "2uTVKRgqdGVqPDxaGwLmhA";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "bmb_connect_page_" + Guid.NewGuid().ToString("N"));
    private readonly StandInNode _node = new();

    public ConnectDevicePageTests()
    {
        Directory.CreateDirectory(_dataPath);
        File.WriteAllText(Path.Combine(_dataPath, ".runtime.json"), """{"frontUrl":"http://front.test"}""");
    }

    public void Dispose()
    {
        _node.Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch (IOException) { }
    }

    /// <summary>What the node answers: closed until <c>POST /node/lan/enable</c>, open for 15 minutes after it.</summary>
    private sealed class StandInNode : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        private bool _open;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method.Method + " " + request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/node/lan/enable") _open = true;
            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/node/lan/disable") _open = false;
            var body = $$"""
                {"mode":"on-demand","active":{{(_open ? "true" : "false")}},"token":"{{Token}}","expiresAt":"2030-01-01T10:15:00+00:00","spkiPin":"{{Pin}}","port":5311,
                 "firewallRule":true,"setting":"off","platform":"windows","firewallManaged":true}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Page(StandInNode node, string dataPath) : ConnectDeviceModel(new ApiClient(new HttpClient()), null!)
    {
        protected override NodeLanClient Client => new(new HttpClient(node), dataPath, "key-of-the-test");
    }

    private Page NewPage() => new(_node, _dataPath);

    [Fact]
    public async Task OpeningThePage_OnlyAsksForTheStatus_AndOpensNothing()
    {
        var page = NewPage();

        await page.OnGetAsync(CancellationToken.None);

        _node.Requests.Should().Equal(["GET /node/lan"], "a page that is merely opened must not open the node to the network");
        page.Lan.Active.Should().BeFalse();
        page.Endpoints.Should().BeEmpty("no join code is shown while the door is closed");
    }

    [Fact]
    public async Task TheButton_IsAPost_AndOpensTheDoor_AndTheNextVisitSeesItOpen()
    {
        var page = NewPage();

        await page.OnPostEnableAsync(CancellationToken.None);

        _node.Requests.Should().Equal(["POST /node/lan/enable"]);
        page.Lan.Active.Should().BeTrue();
        page.Lan.Token.Should().Be(Token);

        var later = NewPage();
        await later.OnGetAsync(CancellationToken.None);
        later.Lan.Active.Should().BeTrue("the status is the node's, not the page's");

        await later.OnPostDisableAsync(CancellationToken.None);
        _node.Requests.Should().EndWith("POST /node/lan/disable");
        later.Lan.Active.Should().BeFalse();
    }
}
