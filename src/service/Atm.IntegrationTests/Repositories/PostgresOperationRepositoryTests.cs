using Atm.Domain;
using Atm.Infrastructure;
using FluentAssertions;
using Npgsql;
using System.Text.Json;

namespace Atm.IntegrationTests.Repositories;

public class PostgresOperationRepositoryTests : RepositoryTestBase
{
    public PostgresOperationRepositoryTests(AtmDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [Theory]
    [InlineData(OperationType.Withdraw)]
    [InlineData(OperationType.Deposit)]
    public async Task SaveAsync_ThenRead_ShouldRoundTripOperationType(OperationType type)
    {
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        var operation = new Operation("40817123", type, 100);

        await repository.SaveAsync(operation);

        Operation loaded = await SingleAsync(repository, "40817123");

        loaded.Type.Should().Be(type);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("1")]
    [InlineData("250.75")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    public async Task SaveAsync_ShouldWriteExactDecimalAmountIntoJsonPayload(string amountText)
    {
        decimal amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        var operation = new Operation("40817123", OperationType.Deposit, amount);

        await repository.SaveAsync(operation);

        JsonElement payload = await ReadRawPayloadAsync("40817123");

        FindProperty(payload, "amount").GetDecimal().Should().Be(amount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("250.75")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    public async Task SaveAsync_ShouldKeepAmountColumnAndJsonPayloadAmountEqual(string amountText)
    {
        decimal amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);

        await repository.SaveAsync(new Operation("40817123", OperationType.Withdraw, amount));

        JsonElement payload = await ReadRawPayloadAsync("40817123");
        decimal column = await ReadRawAmountColumnAsync("40817123");

        FindProperty(payload, "amount").GetDecimal().Should().Be(column);
        column.Should().Be(amount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.01")]
    [InlineData("1")]
    [InlineData("250.75")]
    [InlineData("79228162514264337593543950335")]
    [InlineData("0.0000000000000000000000000001")]
    public async Task SaveAsync_ThenRead_ShouldRoundTripDecimalAmount(string amountText)
    {
        decimal amount = decimal.Parse(amountText, System.Globalization.CultureInfo.InvariantCulture);
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        var operation = new Operation("40817123", OperationType.Deposit, amount);

        await repository.SaveAsync(operation);

        Operation loaded = await SingleAsync(repository, "40817123");

        loaded.Amount.Should().Be(amount);
    }

    [Fact]
    public async Task SaveAsync_WithInvoiceId_ShouldWriteInvoiceIdIntoJsonPayload()
    {
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        var invoiceId = Guid.NewGuid();

        await repository.SaveAsync(new Operation("40817123", OperationType.Deposit, 50, invoiceId));

        JsonElement payload = await ReadRawPayloadAsync("40817123");

        FindProperty(payload, "invoiceId").GetGuid().Should().Be(invoiceId);
    }

    [Fact]
    public async Task SaveAsync_WithoutInvoiceId_ShouldWriteNullInvoiceIdIntoJsonPayload()
    {
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);

        await repository.SaveAsync(new Operation("40817123", OperationType.Deposit, 50));

        JsonElement payload = await ReadRawPayloadAsync("40817123");

        FindProperty(payload, "invoiceId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SaveAsync_WithoutInvoiceId_ShouldRoundTripNullInJsonPayload()
    {
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        var operation = new Operation("40817123", OperationType.Withdraw, 50, invoiceId: null);

        await repository.SaveAsync(operation);

        Operation loaded = await SingleAsync(repository, "40817123");

        loaded.InvoiceId.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_WithInvoiceId_ShouldRoundTripGuidInJsonPayload()
    {
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        var invoiceId = Guid.NewGuid();
        var operation = new Operation("40817123", OperationType.Deposit, 50, invoiceId);

        await repository.SaveAsync(operation);

        Operation loaded = await SingleAsync(repository, "40817123");

        loaded.InvoiceId.Should().Be(invoiceId);
    }

    [Fact]
    public async Task SaveAsync_ShouldPersistAccountNumberAndTimestamp()
    {
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        var timestamp = new DateTime(2024, 5, 17, 10, 30, 0, DateTimeKind.Utc);
        var operation = new Operation("40817999", OperationType.Withdraw, 10, timestamp: timestamp);

        await repository.SaveAsync(operation);

        Operation loaded = await SingleAsync(repository, "40817999");

        loaded.AccountNumber.Should().Be("40817999");
        loaded.Timestamp.Should().BeCloseTo(timestamp, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetByAccountNumberAsync_ShouldOnlyReturnOperationsForThatAccount()
    {
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        await repository.SaveAsync(new Operation("account-a", OperationType.Deposit, 10));
        await repository.SaveAsync(new Operation("account-b", OperationType.Deposit, 20));

        List<Operation> result = await CollectAsync(repository.GetByAccountNumberAsync("account-a", null, 10));

        result.Should().ContainSingle();
        result[0].AccountNumber.Should().Be("account-a");
    }

    [Fact]
    public async Task GetByAccountNumberAsync_WithPagination_ShouldRespectCursorAndOrder()
    {
        var repository = new PostgresOperationRepository(Fixture.ConnectionProvider, Fixture.Serializer);
        await repository.SaveAsync(new Operation("40817123", OperationType.Deposit, 1));
        await repository.SaveAsync(new Operation("40817123", OperationType.Deposit, 2));
        await repository.SaveAsync(new Operation("40817123", OperationType.Deposit, 3));

        List<Operation> firstPage = await CollectAsync(repository.GetByAccountNumberAsync("40817123", null, 2));
        firstPage.Select(o => o.Amount).Should().Equal(1, 2);

        List<Operation> secondPage = await CollectAsync(repository.GetByAccountNumberAsync("40817123", firstPage[^1].Id, 2));
        secondPage.Select(o => o.Amount).Should().Equal(3);
    }

    private static JsonElement FindProperty(JsonElement payload, string name)
    {
        foreach (JsonProperty property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        throw new InvalidOperationException($"Payload has no '{name}' property: {payload}");
    }

    private static async Task<Operation> SingleAsync(PostgresOperationRepository repository, string accountNumber)
    {
        List<Operation> operations = await CollectAsync(repository.GetByAccountNumberAsync(accountNumber, null, 10));
        return operations.Should().ContainSingle().Subject;
    }

    private static async Task<List<Operation>> CollectAsync(IAsyncEnumerable<Operation> source)
    {
        var result = new List<Operation>();

        await foreach (Operation operation in source)
            result.Add(operation);

        return result;
    }

    private async Task<JsonElement> ReadRawPayloadAsync(string accountNumber)
    {
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select payload::text from operations where account_number = @account";
        command.Parameters.AddWithValue("account", accountNumber);

        var json = (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("No operation found"));

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<decimal> ReadRawAmountColumnAsync(string accountNumber)
    {
        await using var connection = new NpgsqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select amount from operations where account_number = @account";
        command.Parameters.AddWithValue("account", accountNumber);

        return (decimal)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("No operation found"));
    }
}
