using Atm.Application;
using Atm.Application.Results;
using Grpc.Core;

namespace Atm.Grpc.Services;

public class InvoiceGrpcService : InvoiceService.InvoiceServiceBase
{
    private readonly IInvoiceService _invoiceService;

    public InvoiceGrpcService(IInvoiceService invoiceService)
    {
        _invoiceService = invoiceService;
    }

    public override async Task<CreateInvoiceResponse> CreateInvoice(CreateInvoiceRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));
        decimal amount = GrpcMapping.ParseAmount(request.Amount, nameof(request.Amount));

        CreateInvoiceResult result = await _invoiceService.CreateInvoiceAsync(sessionKey, request.PayerAccountNumber, amount, request.UserId);

        return result.Status switch
        {
            CreateInvoiceStatus.Success when result.InvoiceId is { } invoiceId => new CreateInvoiceResponse { InvoiceId = invoiceId.ToString() },
            CreateInvoiceStatus.Unauthorized => throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized")),
            CreateInvoiceStatus.PayerNotFound => throw new RpcException(new Status(StatusCode.NotFound, "Payer account not found")),
            CreateInvoiceStatus.InvalidAmount => throw new RpcException(new Status(StatusCode.InvalidArgument, "Amount must be positive")),
            _ => throw new RpcException(new Status(StatusCode.Internal, "Unexpected result")),
        };
    }

    public override async Task<PayInvoiceResponse> PayInvoice(InvoiceRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));
        Guid invoiceId = GrpcMapping.ParseGuid(request.InvoiceId, nameof(request.InvoiceId));

        PayInvoiceResult result = await _invoiceService.PayInvoiceAsync(sessionKey, invoiceId, request.UserId);

        return result switch
        {
            PayInvoiceResult.Success => new PayInvoiceResponse(),
            PayInvoiceResult.Unauthorized => throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized")),
            PayInvoiceResult.NotFound => throw new RpcException(new Status(StatusCode.NotFound, "Invoice not found")),
            PayInvoiceResult.AlreadyProcessed => throw new RpcException(new Status(StatusCode.FailedPrecondition, "Invoice already processed")),
            PayInvoiceResult.InsufficientFunds => throw new RpcException(new Status(StatusCode.FailedPrecondition, "Insufficient funds")),
            _ => throw new RpcException(new Status(StatusCode.Internal, "Unexpected result")),
        };
    }

    public override async Task<CancelInvoiceResponse> CancelInvoice(InvoiceRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));
        Guid invoiceId = GrpcMapping.ParseGuid(request.InvoiceId, nameof(request.InvoiceId));

        CancelInvoiceResult result = await _invoiceService.CancelInvoiceAsync(sessionKey, invoiceId, request.UserId);

        return result switch
        {
            CancelInvoiceResult.Success => new CancelInvoiceResponse(),
            CancelInvoiceResult.Unauthorized => throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized")),
            CancelInvoiceResult.NotFound => throw new RpcException(new Status(StatusCode.NotFound, "Invoice not found")),
            CancelInvoiceResult.AlreadyProcessed => throw new RpcException(new Status(StatusCode.FailedPrecondition, "Invoice already processed")),
            _ => throw new RpcException(new Status(StatusCode.Internal, "Unexpected result")),
        };
    }

    public override async Task<GetInvoicesResponse> GetOutgoingInvoices(GetOutgoingInvoicesRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));
        Guid? cursor = ParseCursor(request.PageToken);
        int pageSize = request.PageSize > 0 ? request.PageSize : 20;
        Atm.Domain.InvoiceStatus? status = request.HasStatus ? FromProto(request.Status) : null;

        GetOutgoingInvoicesResult result = await _invoiceService.GetOutgoingInvoicesAsync(
            sessionKey,
            request.HasPayerAccountNumber ? request.PayerAccountNumber : null,
            status,
            cursor,
            pageSize,
            request.UserId);

        if (result.Status != GetInvoicesStatus.Success || result.Invoices == null)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized"));

        return ToResponse(result.Invoices, result.NextCursor);
    }

    public override async Task<GetInvoicesResponse> GetIncomingInvoices(GetIncomingInvoicesRequest request, ServerCallContext context)
    {
        Guid sessionKey = GrpcMapping.ParseGuid(request.SessionKey, nameof(request.SessionKey));
        Guid? cursor = ParseCursor(request.PageToken);
        int pageSize = request.PageSize > 0 ? request.PageSize : 20;
        Atm.Domain.InvoiceStatus? status = request.HasStatus ? FromProto(request.Status) : null;

        GetIncomingInvoicesResult result = await _invoiceService.GetIncomingInvoicesAsync(
            sessionKey,
            request.HasPayeeAccountNumber ? request.PayeeAccountNumber : null,
            status,
            cursor,
            pageSize,
            request.UserId);

        if (result.Status != GetInvoicesStatus.Success || result.Invoices == null)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Not authorized"));

        return ToResponse(result.Invoices, result.NextCursor);
    }

    private static Guid? ParseCursor(string pageToken)
    {
        return string.IsNullOrEmpty(pageToken) ? null : GrpcMapping.ParseGuid(pageToken, nameof(pageToken));
    }

    private static GetInvoicesResponse ToResponse(IReadOnlyList<Atm.Domain.Invoice> invoices, Guid? nextCursor)
    {
        var response = new GetInvoicesResponse { NextPageToken = nextCursor?.ToString() ?? string.Empty };

        foreach (Atm.Domain.Invoice invoice in invoices)
            response.Invoices.Add(ToProto(invoice));

        return response;
    }

    private static Invoice ToProto(Atm.Domain.Invoice invoice)
    {
        return new Invoice
        {
            Id = invoice.Id.ToString(),
            PayerAccountNumber = invoice.PayerAccountNumber,
            PayeeAccountNumber = invoice.PayeeAccountNumber,
            Amount = GrpcMapping.FormatAmount(invoice.Amount),
            Status = ToProto(invoice.Status),
            CreatedAt = GrpcMapping.ToTimestamp(invoice.CreatedAt),
        };
    }

    private static InvoiceStatus ToProto(Atm.Domain.InvoiceStatus status) => status switch
    {
        Atm.Domain.InvoiceStatus.Created => InvoiceStatus.Created,
        Atm.Domain.InvoiceStatus.Paid => InvoiceStatus.Paid,
        Atm.Domain.InvoiceStatus.Cancelled => InvoiceStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static Atm.Domain.InvoiceStatus FromProto(InvoiceStatus status) => status switch
    {
        InvoiceStatus.Created => Atm.Domain.InvoiceStatus.Created,
        InvoiceStatus.Paid => Atm.Domain.InvoiceStatus.Paid,
        InvoiceStatus.Cancelled => Atm.Domain.InvoiceStatus.Cancelled,
        InvoiceStatus.Unspecified => throw new RpcException(new Status(StatusCode.InvalidArgument, "Invoice status must be specified")),
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "Unknown invoice status")),
    };
}
