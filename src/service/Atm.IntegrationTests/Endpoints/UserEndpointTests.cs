using Atm.Domain;
using Atm.Infrastructure;
using FluentAssertions;
using Users.Grpc.Contracts;

namespace Atm.IntegrationTests.Endpoints;

public class UserEndpointTests : GrpcServiceTestBase
{
    public UserEndpointTests(AtmGrpcServiceFixture grpc)
        : base(grpc)
    {
    }

    [Fact]
    public async Task IsExists_ForKnownUserExternalId_ShouldReturnTrue()
    {
        string userId = $"user-{Guid.NewGuid():N}";
        var repository = new PostgresUserRepository(Grpc.ConnectionProvider);
        await repository.SaveAsync(new User(userId, DateTime.UtcNow));
        long externalId = (await repository.GetExternalIdAsync(userId)).GetValueOrDefault();

        GetUserByIdResponse response = await Grpc.UserClient.IsExistsAsync(new GetUserByIdRequest { UserId = externalId });

        response.IsExists.Should().BeTrue();
    }

    [Fact]
    public async Task IsExists_ForUnknownExternalId_ShouldReturnFalse()
    {
        GetUserByIdResponse response = await Grpc.UserClient.IsExistsAsync(new GetUserByIdRequest { UserId = long.MaxValue });

        response.IsExists.Should().BeFalse();
    }
}
