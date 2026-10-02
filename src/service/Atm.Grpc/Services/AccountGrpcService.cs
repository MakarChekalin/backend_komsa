using Atm.Application;
using Atm.Application.Results;
using Atm.Domain;
using Grpc.Core;
using System.Globalization;

namespace Atm.Grpc.Services;

public class AccountGrpcService : AccountService.AccountServiceBase
{
    private readonly IAccountService _accountService;

    public AccountGrpcService(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public override async Task<CreateAccountResponse> CreateAccount(CreateAccountRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));

        CreateAccountResult result = await _accountService.CreateAccountAsync(sessionKey, request.Number, request.PinCode, request.OwnerUserId);

        return result switch
        {
            CreateAccountResult.Success => new CreateAccountResponse(),
            CreateAccountResult.Unauthorized => throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized")),
            CreateAccountResult.Exists => throw new RpcException(new Status(StatusCode.AlreadyExists, "Account already exists")),
            CreateAccountResult.OwnerNotFound => throw new RpcException(new Status(StatusCode.NotFound, "Owner user not found")),
            CreateAccountResult.AccountLimitExceeded => throw new RpcException(new Status(StatusCode.FailedPrecondition, "Account limit exceeded")),
            _ => throw new RpcException(new Status(StatusCode.Internal, "Unexpected result")),
        };
    }

    public override async Task<WithdrawResponse> Withdraw(AmountRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));
        decimal amount = GrpcMapping.ParseAmount(request.Amount, nameof(request.Amount));

        WithdrawResult result = await _accountService.WithdrawAsync(sessionKey, amount, request.AccountNumber, request.UserId);

        return result switch
        {
            WithdrawResult.Success => new WithdrawResponse(),
            WithdrawResult.Unauthorized => throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized")),
            WithdrawResult.InsufficientFunds => throw new RpcException(new Status(StatusCode.FailedPrecondition, "Insufficient funds")),
            WithdrawResult.InvalidAmount => throw new RpcException(new Status(StatusCode.InvalidArgument, "Amount must be positive")),
            _ => throw new RpcException(new Status(StatusCode.Internal, "Unexpected result")),
        };
    }

    public override async Task<DepositResponse> Deposit(AmountRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));
        decimal amount = GrpcMapping.ParseAmount(request.Amount, nameof(request.Amount));

        DepositResult result = await _accountService.DepositAsync(sessionKey, amount, request.AccountNumber, request.UserId);

        return result switch
        {
            DepositResult.Success => new DepositResponse(),
            DepositResult.Unauthorized => throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized")),
            DepositResult.InvalidAmount => throw new RpcException(new Status(StatusCode.InvalidArgument, "Amount must be positive")),
            _ => throw new RpcException(new Status(StatusCode.Internal, "Unexpected result")),
        };
    }

    public override async Task<GetBalanceResponse> GetBalance(SessionRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));

        GetBalanceResult result = await _accountService.GetBalanceAsync(sessionKey, request.AccountNumber, request.UserId);

        if (result.Status != GetBalanceStatus.Success)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized"));

        return new GetBalanceResponse { Balance = GrpcMapping.FormatAmount(result.Balance) };
    }

    public override async Task<GetHistoryResponse> GetHistory(GetHistoryRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));

        long? cursor = string.IsNullOrEmpty(request.PageToken)
            ? null
            : long.Parse(request.PageToken, CultureInfo.InvariantCulture);

        int pageSize = request.PageSize > 0 ? request.PageSize : 20;

        GetHistoryResult result = await _accountService.GetHistoryAsync(sessionKey, request.AccountNumber, request.UserId, cursor, pageSize);

        if (result.Status != GetHistoryStatus.Success || result.Operations == null)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized"));

        var response = new GetHistoryResponse
        {
            NextPageToken = result.NextCursor?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        };

        foreach (Operation operation in result.Operations)
            response.Operations.Add(ToHistoryItem(operation));

        return response;
    }

    private static OperationHistoryItem ToHistoryItem(Operation operation)
    {
        var item = new OperationHistoryItem { Timestamp = GrpcMapping.ToTimestamp(operation.Timestamp) };

        switch (operation.Type)
        {
            case OperationType.Withdraw:
                var withdraw = new WithdrawOperation { Amount = GrpcMapping.FormatAmount(operation.Amount) };

                if (operation.InvoiceId != null)
                    withdraw.InvoiceId = operation.InvoiceId.Value.ToString();

                item.Withdraw = withdraw;
                break;

            case OperationType.Deposit:
                var deposit = new DepositOperation { Amount = GrpcMapping.FormatAmount(operation.Amount) };

                if (operation.InvoiceId != null)
                    deposit.InvoiceId = operation.InvoiceId.Value.ToString();

                item.Deposit = deposit;
                break;
        }

        return item;
    }
}
