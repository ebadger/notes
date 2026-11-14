using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Notes.Api.Tests.Infrastructure;

namespace Notes.Api.Tests.Integration;

[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RequestSizeTests(PostgreSqlFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData("create", false, 1048576, 201)]
    [InlineData("create", false, 1048577, 413)]
    [InlineData("create", true, 1048576, 201)]
    [InlineData("create", true, 1048577, 413)]
    [InlineData("content", false, 1048576, 200)]
    [InlineData("content", false, 1048577, 413)]
    [InlineData("content", true, 1048576, 200)]
    [InlineData("content", true, 1048577, 413)]
    [InlineData("sharing", false, 1048576, 200)]
    [InlineData("sharing", false, 1048577, 413)]
    [InlineData("sharing", true, 1048576, 200)]
    [InlineData("sharing", true, 1048577, 413)]
    public async Task Exact_request_size_limit_is_enforced_by_real_http_framing(
        string operation,
        bool streamed,
        int bytes,
        int expectedStatus)
    {
        var original = operation == "create" ? null : await CreateNoteAsync();
        var prefix = operation == "sharing"
            ? """{"grants":[]}"""
            : """{"title":"Request size boundary","body":""}""";
        var encoded = Encoding.UTF8.GetBytes(prefix);
        var payload = new byte[bytes];
        Array.Fill(payload, (byte)' ');
        encoded.CopyTo(payload, 0);
        Assert.Equal(bytes, payload.Length);

        using var content = streamed
            ? (HttpContent)new UnbufferedContent(payload)
            : new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        Assert.Equal(streamed ? null : bytes, content.Headers.ContentLength);
        var path = operation == "create"
            ? "/api/notes"
            : $"/api/notes/{original!.Note.Id}{(operation == "sharing" ? "/sharing" : "")}";
        using var request = new HttpRequestMessage(operation == "create" ? HttpMethod.Post : HttpMethod.Put, path)
        {
            Content = content,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (original is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", original.ETag);
        }

        if (streamed)
        {
            request.Headers.TransferEncodingChunked = true;
        }

        using var response = await Alice.SendAsync(request);
        if (expectedStatus == 413)
        {
            await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
            Assert.True(response.Headers.ConnectionClose,
                "HTTP/1.1 oversized requests must not leave an unread body on a reusable connection.");
            if (original is not null)
            {
                Assert.Equal(original, await GetNoteAsync(Alice, original.Note.Id));
            }
        }
        else
        {
            Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
            AssertNoStore(response);
        }
    }

    private sealed class UnbufferedContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            for (var offset = 0; offset < payload.Length; offset += 8192)
            {
                await stream.WriteAsync(payload.AsMemory(offset, Math.Min(8192, payload.Length - offset)),
                    cancellationToken);
            }
        }
    }
}
