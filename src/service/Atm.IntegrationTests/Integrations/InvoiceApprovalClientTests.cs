using Atm.Application;
using Atm.Grpc.Integrations;
using FluentAssertions;
using Grpc.Core;
using Invoices.Grpc.Contracts;

namespace Atm.IntegrationTests.Integrations;

public class InvoiceApprovalClientTests
{
    [Fact]
    public async Task AssignAccountantAsync_ShouldSendIdsAndReturnSuccess()
    {
        var invoker = new FakeCallInvoker();

        InvoiceApprovalCallResult result = await CreateClient(invoker).AssignAccountantAsync(10, 20, CancellationToken.None);

        result.Should().Be(InvoiceApprovalCallResult.Success);
        invoker.LastMethod.Should().Be("AssignAccountant");
        var request = invoker.LastRequest.Should().BeOfType<AssignAccountantRequest>().Subject;
        request.InvoiceId.Should().Be(10);
        request.UserId.Should().Be(20);
    }

    [Fact]
    public async Task ApproveInvoiceAsync_ShouldSendIdsAndReturnSuccess()
    {
        var invoker = new FakeCallInvoker();

        InvoiceApprovalCallResult result = await CreateClient(invoker).ApproveInvoiceAsync(11, 21, CancellationToken.None);

        result.Should().Be(InvoiceApprovalCallResult.Success);
        invoker.LastMethod.Should().Be("ApproveInvoice");
        var request = invoker.LastRequest.Should().BeOfType<ApproveInvoiceRequest>().Subject;
        request.InvoiceId.Should().Be(11);
        request.UserId.Should().Be(21);
    }

    [Fact]
    public async Task DeclineInvoiceAsync_ShouldSendIdsAndReturnSuccess()
    {
        var invoker = new FakeCallInvoker();

        InvoiceApprovalCallResult result = await CreateClient(invoker).DeclineInvoiceAsync(12, 22, CancellationToken.None);

        result.Should().Be(InvoiceApprovalCallResult.Success);
        invoker.LastMethod.Should().Be("DeclineInvoice");
        var request = invoker.LastRequest.Should().BeOfType<DeclineInvoiceRequest>().Subject;
        request.InvoiceId.Should().Be(12);
        request.UserId.Should().Be(22);
    }

    [Theory]
    [InlineData(StatusCode.NotFound, InvoiceApprovalCallResult.NotFound)]
    [InlineData(StatusCode.FailedPrecondition, InvoiceApprovalCallResult.InvalidState)]
    [InlineData(StatusCode.Unavailable, InvoiceApprovalCallResult.ServiceUnavailable)]
    [InlineData(StatusCode.DeadlineExceeded, InvoiceApprovalCallResult.ServiceUnavailable)]
    [InlineData(StatusCode.Internal, InvoiceApprovalCallResult.ServiceUnavailable)]
    public async Task AllOperations_ShouldMapRpcFailuresToCallResults(StatusCode status, InvoiceApprovalCallResult expected)
    {
        var invoker = new FakeCallInvoker(new RpcException(new Status(status, "failure")));
        InvoiceApprovalClient client = CreateClient(invoker);

        (await client.AssignAccountantAsync(1, 2, CancellationToken.None)).Should().Be(expected);
        (await client.ApproveInvoiceAsync(1, 2, CancellationToken.None)).Should().Be(expected);
        (await client.DeclineInvoiceAsync(1, 2, CancellationToken.None)).Should().Be(expected);
    }

    private static InvoiceApprovalClient CreateClient(CallInvoker invoker)
    {
        return new InvoiceApprovalClient(new Invoices.Grpc.Contracts.InvoiceService.InvoiceServiceClient(invoker));
    }

    private sealed class FakeCallInvoker : CallInvoker
    {
        private readonly RpcException? _failure;

        public FakeCallInvoker(RpcException? failure = null)
        {
            _failure = failure;
        }

        public string? LastMethod { get; private set; }

        public object? LastRequest { get; private set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            LastMethod = method.Name;
            LastRequest = request;

            Task<TResponse> response = _failure != null
                ? Task.FromException<TResponse>(_failure)
                : Task.FromResult(Activator.CreateInstance<TResponse>());

            return new AsyncUnaryCall<TResponse>(
                response,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            throw new NotSupportedException();
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            throw new NotSupportedException();
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options)
        {
            throw new NotSupportedException();
        }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options)
        {
            throw new NotSupportedException();
        }
    }
}
