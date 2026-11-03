using Hihooi.Redis;

namespace Hihooi.Tests.Redis;

public class RedisBufferGuardTests
{
    [Fact]
    public void Ctor_rejects_null_multiplexer()
    {
        Assert.Throws<ArgumentNullException>(() => new RedisTransactionsBuffer(null!));
    }
}
