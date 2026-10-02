using Atm.Domain;
using Itmo.Dev.Platform.Persistence.Abstractions.Commands;
using Itmo.Dev.Platform.Persistence.Abstractions.Connections;
using System.Data.Common;

namespace Atm.Infrastructure;

public class PostgresAccountRepository : IAccountRepository
{
    private readonly IPersistenceConnectionProvider _connectionProvider;

    public PostgresAccountRepository(IPersistenceConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider;
    }

    public Task<Account?> GetByNumberAsync(string number)
    {
        return GetByNumberAsync(number, forUpdate: false);
    }

    public Task<Account?> GetByNumberForUpdateAsync(string number)
    {
        return GetByNumberAsync(number, forUpdate: true);
    }

    public async Task<long> SaveAsync(Account account)
    {
        await using IPersistenceConnection connection = await _connectionProvider.GetConnectionAsync(CancellationToken.None);

        await using IPersistenceCommand command = connection
            .CreateCommand("""
                insert into accounts (number, pin_code, user_id, balance, account_type)
                values (@number, @pinCode, @userId, @balance, @accountType)
                on conflict (number) do update set balance = @balance
                returning external_id
                """)
            .AddParameter<string>("number", account.Number)
            .AddParameter<string>("pinCode", account.PinCode)
            .AddParameter<string>("userId", account.UserId)
            .AddParameter("balance", account.Balance)
            .AddParameter<string>("accountType", account.Type.ToString());

        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        await reader.ReadAsync();

        return reader.GetInt64(0);
    }

    public async Task<bool> ExistsAsync(string number)
    {
        Account? account = await GetByNumberAsync(number);
        return account != null;
    }

    public async Task<int> CountByUserIdAsync(string userId)
    {
        await using IPersistenceConnection connection = await _connectionProvider.GetConnectionAsync(CancellationToken.None);

        await using IPersistenceCommand command = connection
            .CreateCommand("select count(*) from accounts where user_id = @userId")
            .AddParameter<string>("userId", userId);

        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        await reader.ReadAsync();

        return checked((int)reader.GetInt64(0));
    }

    private async Task<Account?> GetByNumberAsync(string number, bool forUpdate)
    {
        await using IPersistenceConnection connection = await _connectionProvider.GetConnectionAsync(CancellationToken.None);

        string lockClause = forUpdate ? "for update" : string.Empty;

        await using IPersistenceCommand command = connection
            .CreateCommand($"""
                select number, pin_code, user_id, balance, account_type, external_id
                from accounts
                where number = @number
                {lockClause}
                """)
            .AddParameter<string>("number", number);

        await using DbDataReader reader = await command.ExecuteReaderAsync(CancellationToken.None);

        if (!await reader.ReadAsync())
            return null;

        return new Account(
            reader.GetString(reader.GetOrdinal("number")),
            reader.GetString(reader.GetOrdinal("pin_code")),
            reader.GetString(reader.GetOrdinal("user_id")),
            reader.GetDecimal(reader.GetOrdinal("balance")),
            Enum.Parse<AccountType>(reader.GetString(reader.GetOrdinal("account_type"))),
            reader.GetInt64(reader.GetOrdinal("external_id")));
    }
}
