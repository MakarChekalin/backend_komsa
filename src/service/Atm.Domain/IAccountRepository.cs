namespace Atm.Domain;

public interface IAccountRepository // перешел на async(что бы пока шел запрос до дб программа не останавливалась)
{
    Task<Account?> GetByNumberAsync(string number);

    Task<Account?> GetByNumberForUpdateAsync(string number);

    Task<long> SaveAsync(Account account);

    Task<bool> ExistsAsync(string number); // сущ?

    Task<int> CountByUserIdAsync(string userId);
}