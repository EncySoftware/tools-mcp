using System.Net;
using ToolsMcp;
using Xunit;

public class ToolsApiTests
{
    private const string Library = """
        {"id":"L1","name":"Kept","createdAt":"2026-10-22T07:00:00Z","updatedAt":"2026-10-22T07:05:00Z",
         "items":[{"component":"c1","name":"10mm Drill","kit":"drills","present":false,"kind":"Drill",
                   "kitSlug":"drills","kitName":"Drills","diameterMm":10.0,"lengthMm":70.0,
                   "picture":false,"drawing":false,"custom":null}]}
        """;

    [Fact]
    public async Task EveryCallCarriesThePersonsToken()
    {
        var stub = new StubHandler().OnJson("GET", "/api/me", """{"sub":"s1","username":"alice","email":"alice@example.com"}""");

        var me = await StubHandler.Api(stub).GetMe("t1");

        Assert.Equal("alice", me.Username);
        Assert.Equal("Bearer t1", stub.Requests[0].Authorization);
    }

    /** The agent's words become the API's: section, for, pullable - and a filter not given is not sent at all. */
    [Fact]
    public async Task SearchSendsOnlyTheFiltersGivenUnderTheApisNames()
    {
        var stub = new StubHandler().OnJson("GET", "/api/components", """{"total":0,"page":0,"size":50,"items":[]}""");

        await StubHandler.Api(stub).FindComponents(
            new ComponentQuery(Query: "фреза Ø10", Buildable: true, MachineType: "mill", Size: 50), "t");

        string url = stub.Requests[0].Uri.OriginalString;
        Assert.Contains("q=%D1%84%D1%80%D0%B5%D0%B7%D0%B0%20%C3%9810", url);
        Assert.Contains("pullable=true", url);
        Assert.Contains("for=mill", url);
        Assert.Contains("page=0", url);
        Assert.Contains("size=50", url);
        Assert.DoesNotContain("kit=", url);
    }

    [Fact]
    public async Task AnUnknownComponentIsNullNotAnError()
    {
        var stub = new StubHandler().On("GET", "/api/components/c9", HttpStatusCode.NotFound,
            """{"status":404,"message":"component not found"}""");

        Assert.Null(await StubHandler.Api(stub).GetComponent("c9", "t"));
    }

    [Fact]
    public async Task AnExpiredSignInIsAnHttpErrorWithItsStatus()
    {
        var stub = new StubHandler().On("GET", "/api/kits", HttpStatusCode.Unauthorized);

        var e = await Assert.ThrowsAsync<ToolsHttpException>(() => StubHandler.Api(stub).ListKits(null, null, null, "t"));
        Assert.Equal(401, e.Status);
    }

    // ------------------------------------------------------------------ a file's size on the hosted server

    private const long MB = 1024 * 1024;

    /**
     * The hosted server holds a file in memory before it keeps it, in a 512 MB container: each file used to be held twice,
     * and only then measured (final review, 2026-10-22). One over the limit is refused by its length, unread.
     */
    [Fact]
    public async Task AFileOverTheCapIsRefusedBeforeItsBodyIsRead()
    {
        var body = new Unread(MB + 1);
        var stub = new StubHandler().OnContent("POST", "/api/pull", () => body);
        var api = new ToolsApi(new HttpClient(stub), "https://tools.test/api") { MaxFile = MB };

        var e = await Assert.ThrowsAsync<DownloadStore.FullException>(() => api.BuildKit("Mills", ["c1"], [], null, "t"));

        Assert.Equal("a file holds at most 1 MB", e.Message);
        Assert.False(body.WasRead);
    }

    /** A chunked answer names no length: it is read up to the cap and no further. */
    [Fact]
    public async Task AFileWithoutALengthStopsAtTheCap()
    {
        var body = new Unsized(10 * MB);
        var stub = new StubHandler().OnContent("POST", "/api/pull", () => body);
        var api = new ToolsApi(new HttpClient(stub), "https://tools.test/api") { MaxFile = MB };

        var e = await Assert.ThrowsAsync<DownloadStore.FullException>(() => api.BuildKit("Mills", ["c1"], [], null, "t"));

        Assert.Equal("a file holds at most 1 MB", e.Message);
        Assert.True(body.Given <= MB + 128 * 1024, $"read {body.Given} bytes");
    }

    [Fact]
    public async Task AFileWithinTheCapComesWhole()
    {
        var stub = new StubHandler()
            .OnContent("POST", "/api/pull", () => new ByteArrayContent(Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray()))
            .OnContent("GET", "/api/kits/mills/download", () => new Unsized(3000));
        var api = new ToolsApi(new HttpClient(stub), "https://tools.test/api") { MaxFile = MB };

        Assert.Equal(Enumerable.Range(0, 1000).Select(i => (byte)i), (await api.BuildKit("Mills", ["c1"], [], null, "t")).Bytes);
        Assert.Equal(3000, (await api.DownloadKit("mills", "t")).Bytes.Length);
    }

    /** A body that says how long it is and must not be read. */
    private sealed class Unread(long length) : HttpContent
    {
        public bool WasRead { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            WasRead = true;
            throw new InvalidOperationException("the body was read");
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            WasRead = true;
            throw new InvalidOperationException("the body was read");
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }

    /** A body of zeros that names no length, handed out as it is read and counted. */
    private sealed class Unsized(long size) : HttpContent
    {
        private readonly Zeros _stream = new(size);
        public long Given => _stream.Given;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class Zeros(long size) : Stream
    {
        public long Given { get; private set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, size - Given);
            Array.Clear(buffer, offset, n);
            Given += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Given; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /** A library named in Cyrillic comes back as filename*=UTF-8''… and is saved under that name, not its escapes. */
    [Fact]
    public async Task AFileKeepsTheNameTheServerGaveIt()
    {
        var stub = new StubHandler().On("POST", "/api/me/libraries/L1/pull", HttpStatusCode.OK, "kit-bytes",
            "attachment; filename*=UTF-8''%D0%A2%D0%BE%D0%BA%D0%B0%D1%80%D0%BA%D0%B0.mKit", "application/octet-stream");

        var file = await StubHandler.Api(stub).ExportLibrary("L1", null, "t");

        Assert.Equal("Токарка.mKit", file.FileName);
        Assert.Equal("kit-bytes"u8.ToArray(), file.Bytes);
    }

    [Fact]
    public async Task APlainFileNameIsKept()
    {
        var stub = new StubHandler().On("GET", "/api/kits/drills/download", HttpStatusCode.OK, "db",
            "attachment; filename=\"Drills_D1-20.mKit\"", "application/octet-stream");

        Assert.Equal("Drills_D1-20.mKit", (await StubHandler.Api(stub).DownloadKit("drills", "t")).FileName);
    }

    [Fact]
    public async Task RemoveSendsTheItemsToTheRemoveRoute()
    {
        var stub = new StubHandler().OnJson("POST", "/api/me/libraries/L1/items/remove", Library);

        await StubHandler.Api(stub).RemoveFromLibrary("L1", [new LibraryEntry(Component: "c1")], "t");

        Assert.Equal(HttpMethod.Post, stub.Requests[0].Method);
        Assert.Equal("""{"items":[{"component":"c1"}]}""", stub.Requests[0].Body);
    }

    /** A PUT with items re-checks them all on the server; a rename sends the name and nothing else. */
    [Fact]
    public async Task RenameSendsTheNameOnly()
    {
        var stub = new StubHandler().OnJson("PUT", "/api/me/libraries/L1", Library);

        await StubHandler.Api(stub).RenameLibrary("L1", "New", "t");

        Assert.Equal("""{"name":"New"}""", stub.Requests[0].Body);
    }

    [Fact]
    public async Task ParsesALibraryWithAWithdrawnItem()
    {
        var stub = new StubHandler().OnJson("GET", "/api/me/libraries/L1", Library);

        var library = await StubHandler.Api(stub).GetLibrary("L1", "t");

        Assert.NotNull(library);
        Assert.False(library.Items[0].Present);
        Assert.Equal("10mm Drill", library.Items[0].Name);
    }

    /** An agent passes "" for a filter it does not mean: sent as kit=, it filtered for a kit with no name. */
    [Fact]
    public async Task BlankFiltersAreNotSent()
    {
        var stub = new StubHandler().OnJson("GET", "/api/components", """{"total":0,"page":0,"size":20,"items":[]}""");

        await StubHandler.Api(stub).FindComponents(new ComponentQuery(Query: "", Kit: "  ", Maker: "Iscar"), "t");

        string url = stub.Requests[0].Uri.OriginalString;
        Assert.Contains("maker=Iscar", url);
        Assert.DoesNotContain("q=", url);
        Assert.DoesNotContain("kit=", url);
    }
}
