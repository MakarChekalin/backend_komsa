using Atm.Domain;
using Itmo.Dev.Platform.Persistence.Abstractions.Commands;
using Itmo.Dev.Platform.Persistence.Abstractions.Connections;
using System.Data.Common;

namespace Atm.Infrastructure;

public class PostgresInvoiceRepository : IInvoiceRepository
{
    private readonly IPersistenceConnectionProvider _connectionProvider;

    public PostgresInvoiceRepository(IPersistenceConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public Task<Invoice?> GetByIdAsync(Guid id)
    {
        return GetSingleAsync("id = @key", "key", id, forUpdate: false);
    }

    public Task<Invoice?> GetByIdForUpdateAsync(Guid id)
    {
        return GetSingleAsync("id = @key", "key", id, forUpdate: true);
    }

    public Task<Invoice?> GetByExternalIdAsync(long externalId)
    {
        return GetSingleAsync("external_id = @key", "key", externalId, forUpdate: false);
    }

    public Task<Invoice?> GetByExternalIdForUpdateAsync(long externalId)
    {
        return GetSingleAsync("external_id = @key", "key", externalId, forUpdate: true);
    }

    public async Task<long> SaveAsync(Invoice invoice)
    {
        await using IPersistenceConnection connection = await _connectionProvider.GetConnectionAsync(CancellationToken.None);

        await using IPersistenceCommand command = connection
            .CreateCommand("""
                insert into invoices (id, payer_account_number, payee_account_number, amount, status, created_at)
                values (@id, @payerAccountNumber, @payeeAccountNumber, @amount, @status, @createdAt)
                on conflict (id) do update set status = @status
                returning external_id
                """)
            .AddParameter("id", invoice.Id)
            .AddParameter<string>("payerAccountNumber", invoice.PayerAccountNumber)
            .AddParameter<string>("payeeAccountNumber", invoice.PayeeAccountNumber)
            .AddParameter("amount", invoice.Amount)
            .AddParameter<string>("status", invoice.Status.ToString())
            .AddParameter("createdAt", invoice.CreatedAt);

        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        await reader.ReadAsync();

        return reader.GetInt64(0);
    }

    public IAsyncEnumerable<Invoice> GetOutgoingAsync(string payeeAccountNumber, string? payerAccountNumber, InvoiceStatus? status, Guid? cursor, int pageSize)
    {
        return StreamAsync("i.payee_account_number = @self", "i.payer_account_number = @counterparty", payeeAccountNumber, payerAccountNumber, status, cursor, pageSize);
    }

    public IAsyncEnumerable<Invoice> GetIncomingAsync(string payerAccountNumber, string? payeeAccountNumber, InvoiceStatus? status, Guid? cursor, int pageSize)
    {
        return StreamAsync("i.payer_account_number = @self", "i.payee_account_number = @counterparty", payerAccountNumber, payeeAccountNumber, status, cursor, pageSize);
    }

    private static Invoice ReadInvoice(DbDataReader reader)
    {
        var status = Enum.Parse<InvoiceStatus>(reader.GetString(reader.GetOrdinal("status")));

        return new Invoice(
            reader.GetString(reader.GetOrdinal("payer_account_number")),
            reader.GetString(reader.GetOrdinal("payee_account_number")),
            reader.GetDecimal(reader.GetOrdinal("amount")),
            reader.GetGuid(reader.GetOrdinal("id")),
            status,
            reader.GetDateTime(reader.GetOrdinal("created_at")),
            reader.GetInt64(reader.GetOrdinal("external_id")));
    }

    private async Task<Invoice?> GetSingleAsync<TKey>(string condition, string parameterName, TKey value, bool forUpdate)
        where TKey : notnull
    {
        await using IPersistenceConnection connection = await _connectionProvider.GetConnectionAsync(CancellationToken.None);

        string lockClause = forUpdate ? "for update" : string.Empty;

        await using IPersistenceCommand command = connection
            .CreateCommand($"""
                select id, payer_account_number, payee_account_number, amount, status, created_at, external_id
                from invoices
                where {condition}
                {lockClause}
                """)
            .AddParameter(parameterName, value);

        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        if (!await reader.ReadAsync())
            return null;

        return ReadInvoice(reader);
    }

    private async IAsyncEnumerable<Invoice> StreamAsync(
        string selfFilter,
        string counterpartyFilter,
        string self,
        string? counterparty,
        InvoiceStatus? status,
        Guid? cursor,
        int pageSize)
    {
        await using IPersistenceConnection connection = await _connectionProvider.GetConnectionAsync(CancellationToken.None);

        await using IPersistenceCommand command = connection
            .CreateCommand($"""
                select i.id, i.payer_account_number, i.payee_account_number, i.amount, i.status, i.created_at, i.external_id
                from invoices i
                left join invoices c on c.id = @cursor
                where {selfFilter}
                  and (@counterparty is null or {counterpartyFilter})
                  and (@status is null or i.status = @status)
                  and (@cursor is null or (i.created_at, i.id) > (c.created_at, c.id))
                order by i.created_at, i.id
                limit @pageSize
                """)
            .AddParameter<string>("self", self)
            .AddParameter<string?>("counterparty", counterparty)
            .AddParameter<string?>("status", status?.ToString())
            .AddParameter<Guid?>("cursor", cursor)
            .AddParameter("pageSize", pageSize);

        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        while (await reader.ReadAsync())
            yield return ReadInvoice(reader);
    }
}
