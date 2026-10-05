using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Payments controller — direct-to-business payments (cash + M-PESA via Daraja).
/// Base route: /v1/payments. Convention mirrors AppointmentController: [Authorize]
/// at controller level, action-level role restrictions, private GetUserId() reading
/// the userId claim, ApiResponse envelope, MapFailure mapping.
/// Amounts are ALWAYS computed server-side; the client never sends money values.
/// </summary>
[ApiController]
[Route("v1/payments")]
[Produces("application/json")]
[Authorize]
[RequireModule("payments")]
[EnableRateLimiting("general")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _payments;

    public PaymentsController(IPaymentService payments) => _payments = payments;

    /// <summary>Create a payment for an appointment (customer pays, or staff/business records an intent).</summary>
    [HttpPost]
    [Authorize(Roles = "Customer,Staff,Business")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreatePaymentRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.CreatePaymentAsync(request, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Get one payment (tenant-isolated).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid id)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.GetPaymentAsync(id, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>List payments with filters (tenant-scoped by role).</summary>
    [HttpGet]
    [Authorize(Roles = "Customer,Staff,Business,Admin")]
    [ProducesResponseType(typeof(ApiResponse<PaymentListResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? appointmentId, [FromQuery] Guid? customerId, [FromQuery] string? status,
        [FromQuery] string? method, [FromQuery] string? type, [FromQuery] DateTime? from,
        [FromQuery] DateTime? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.ListPaymentsAsync(new PaymentListQuery
        {
            AppointmentId = appointmentId, CustomerId = customerId, Status = status,
            Method = method, Type = type, From = from, To = to, Page = page, PageSize = pageSize
        }, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Server-computed payment summary for an appointment (total/paid/balance/status).</summary>
    [HttpGet("appointment/{appointmentId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AppointmentPaymentSummary>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAppointmentSummary(Guid appointmentId)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.GetAppointmentPaymentSummaryAsync(appointmentId, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Business finance dashboard aggregates.</summary>
    [HttpGet("dashboard")]
    [Authorize(Roles = "Business,Staff,Admin")]
    [ProducesResponseType(typeof(ApiResponse<PaymentDashboardResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Dashboard()
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.GetDashboardAsync(userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Authorised staff/business confirms physical cash was received (audited, idempotent).</summary>
    [HttpPost("{id:guid}/confirm-cash")]
    [Authorize(Roles = "Staff,Business")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ConfirmCash(Guid id, [FromBody] ConfirmCashRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.ConfirmCashAsync(id, request.Note, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Retry a failed/expired payment with a new attempt (max attempts enforced).</summary>
    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Retry(Guid id)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.RetryPaymentAsync(id, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Cancel a payment that has not succeeded.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.CancelPaymentAsync(id, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Reverse/refund a successful payment (business owner only; audited).</summary>
    [HttpPost("{id:guid}/reverse")]
    [Authorize(Roles = "Business")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reverse(Guid id, [FromBody] ReversePaymentRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();
        var result = await _payments.ReversePaymentAsync(id, request.Reason, userId.Value);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
            "INVALID_STATUS_TRANSITION" or "PROVIDER_ERROR" => Conflict(result),
            _ => BadRequest(result)
        };

    private (Guid? userId, string? role) GetIdentity()
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                   ?? User.FindFirst("role")?.Value;
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? (id, role) : (null, role);
    }
}
