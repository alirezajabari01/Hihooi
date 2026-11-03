using Hihooi.Core.Diagnostics.Tracing;

namespace Hihooi.Tests.Observability;

public class TraceMaskingAndStoreTests
{
    [Fact]
    public void MaskParameters_redacts_password_like_names()
    {
        var masked = TraceMasking.MaskParameters(
            new Dictionary<string, object?> { ["Password"] = "secret", ["Id"] = 15 },
            maskSensitive: true,
            maxBytes: 4096);
        Assert.Equal("***", masked["Password"]);
        Assert.Equal("15", masked["Id"]);
    }

    [Fact]
    public async Task FileTraceStore_survives_reload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hihooi-traces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await using (var store = new FileTraceStore(dir, capacity: 100, retention: TimeSpan.FromHours(1)))
            {
                store.Complete(new HihooiTrace
                {
                    TraceId = "abc123",
                    RequestId = "r1",
                    Operation = "ExecuteAsync",
                    Kind = RequestKind.Write,
                    StartUtc = DateTime.UtcNow,
                    EndUtc = DateTime.UtcNow,
                    DurationMs = 12,
                    Status = SpanStatus.Ok,
                    SelectedNodeId = "primary",
                    Spans = { new HihooiSpan { SpanId = "s1", Name = "api.entry", Component = "test", StartUtc = DateTime.UtcNow } }
                });
                await Task.Delay(400);
            }

            await using var store2 = new FileTraceStore(dir, capacity: 100, retention: TimeSpan.FromHours(1));
            var loaded = store2.Get("abc123");
            Assert.NotNull(loaded);
            Assert.Equal("primary", loaded!.SelectedNodeId);
            Assert.Equal(RequestKind.Write, loaded.Kind);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void SqlShapeNormalizer_collapses_literals()
    {
        var shape = SqlShapeNormalizer.Normalize("SELECT * FROM Products WHERE Id = 15 AND Name = 'x'");
        Assert.Contains("?", shape);
        Assert.DoesNotContain("15", shape);
    }
}
