using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Service owning all booking/appointment logic. Caller identity + role are always
/// passed in and never trusted from request DTOs; businessId is derived server-side
/// whenever the caller's tenant can be resolved locally.
/// </summary>
public interface IAppointmentService
{
    /// <summary>Computes bookable slots for a business across a date range.</summary>
    Task<ApiResponse<List<AvailabilitySlotResponse>>> GetAvailableSlotsAsync(Guid userId, string role, Guid businessId, AvailabilityQueryRequest request);

    /// <summary>
    /// Full bookable grid (including blocked slots, per-day rollups and
    /// alternative-staff suggestions) powering the booking wizard's time step.
    /// </summary>
    Task<ApiResponse<AvailabilityCalendarResponse>> GetAvailabilityCalendarAsync(Guid userId, string role, Guid businessId, AvailabilityQueryRequest request);

    /// <summary>Customer self-service booking. CustomerId is forced to the caller.</summary>
    Task<ApiResponse<AppointmentResponse>> CreateAppointmentAsync(Guid callerUserId, string role, CreateAppointmentRequest request);

    /// <summary>Business/Staff booking on behalf of a customer.</summary>
    Task<ApiResponse<AppointmentResponse>> CreateAppointmentOnBehalfAsync(Guid callerUserId, string role, CreateAppointmentOnBehalfRequest request);

    /// <summary>Reschedules an existing appointment inside a transactional overlap guard. Customer calls create a pending request instead.</summary>
    Task<ApiResponse<AppointmentResponse>> RescheduleAsync(Guid callerUserId, string role, Guid appointmentId, RescheduleAppointmentRequest request);

    /// <summary>Business confirms a customer's pending reschedule request; the proposed changes are applied.</summary>
    Task<ApiResponse<AppointmentResponse>> ApproveRescheduleRequestAsync(Guid callerUserId, string role, Guid appointmentId);

    /// <summary>Business rejects a customer's pending reschedule request; the appointment is unchanged.</summary>
    Task<ApiResponse<AppointmentResponse>> RejectRescheduleRequestAsync(Guid callerUserId, string role, Guid appointmentId, string? reason = null);

    /// <summary>The requester withdraws their own pending reschedule request.</summary>
    Task<ApiResponse<AppointmentResponse>> WithdrawRescheduleRequestAsync(Guid callerUserId, string role, Guid appointmentId);

    /// <summary>
    /// Sweeps undecided reschedule requests past their expiry into the
    /// <c>expired</c> terminal state and notifies both parties. Idempotent, so a
    /// repeated or concurrent sweep is harmless. Driven by the background expiry
    /// worker; returns the number of requests expired.
    /// </summary>
    Task<int> ExpireStaleRescheduleRequestsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The preserved history of every rescheduling proposal for an appointment,
    /// including rejected, withdrawn and expired ones.
    /// </summary>
    Task<ApiResponse<List<AppointmentRescheduleHistoryItemResponse>>> GetRescheduleHistoryAsync(
        Guid callerUserId, string role, Guid appointmentId);

    /// <summary>Cancels an existing appointment.</summary>
    Task<ApiResponse<AppointmentResponse>> CancelAsync(Guid callerUserId, string role, Guid appointmentId, CancelAppointmentRequest request);

    /// <summary>Transitions a booked appointment to confirmed.</summary>
    Task<ApiResponse<AppointmentResponse>> ConfirmAsync(Guid callerUserId, string role, Guid appointmentId);

    /// <summary>Transitions a confirmed appointment to completed.</summary>
    Task<ApiResponse<AppointmentResponse>> CompleteAsync(Guid callerUserId, string role, Guid appointmentId);

    /// <summary>Transitions a confirmed appointment to no_show.</summary>
    Task<ApiResponse<AppointmentResponse>> MarkNoShowAsync(Guid callerUserId, string role, Guid appointmentId);

    /// <summary>Toggles the customer's reminder opt-out on an appointment.</summary>
    Task<ApiResponse<AppointmentResponse>> ToggleReminderOptOutAsync(Guid callerUserId, string role, Guid appointmentId, bool optOut);

    /// <summary>
    /// Lists the caller's own appointments, DB-first: filters + sorting + pagination
    /// all happen in the database. Only the requested page is returned.
    /// </summary>
    Task<ApiResponse<PaginatedResponse<AppointmentResponse>>> GetCustomerAppointmentsAsync(
        Guid customerId, CustomerAppointmentsQueryRequest request);

    /// <summary>
    /// Distinct filter facets (businesses, staff, services) for the customer's own
    /// appointment filter UI — cheap DB projections, never a full-list load.
    /// </summary>
    Task<ApiResponse<CustomerAppointmentFiltersResponse>> GetCustomerAppointmentFiltersAsync(Guid customerId);

    /// <summary>Gets a single appointment, asserting caller tenant ownership.</summary>
    Task<ApiResponse<AppointmentResponse>> GetAppointmentAsync(Guid callerUserId, string role, Guid appointmentId);

    /// <summary>
    /// Replaces the business's working note on an appointment. Authorized exactly
    /// like a status transition (owner, or the assigned staff member), and
    /// recorded in the status history so the edit is auditable.
    /// </summary>
    Task<ApiResponse<AppointmentResponse>> UpdateNoteAsync(
        Guid callerUserId, string role, Guid appointmentId, string? note);

    /// <summary>
    /// The appointment's persisted status-change history, newest first — the
    /// audit trail every transition already writes. Read-only.
    /// </summary>
    Task<ApiResponse<List<AppointmentActivityItemResponse>>> GetActivityAsync(
        Guid callerUserId, string role, Guid appointmentId);

    /// <summary>Paged business appointment list with filters.</summary>

    /// <summary>
    /// Owner-dashboard appointment list with DB-first filtering, sorting,
    /// pagination and lightweight reference data for filters. Resolves customer/
    /// staff/service names server-side; only the requested page plus the
    /// reference buckets are returned.
    /// </summary>
    Task<ApiResponse<AppointmentDashboardResponse>> GetAppointmentDashboardAsync(
        Guid ownerUserId,
        DateTime? from,
        DateTime? to,
        Guid? staffUserId,
        Guid? customerId,
        Guid? serviceId,
        string? status,
        string? search,
        string sortBy,
        string sortDirection,
        int page,
        int pageSize);

    Task<ApiResponse<PaginatedResponse<AppointmentResponse>>> GetBusinessAppointmentsAsync(Guid ownerUserId, string? status, DateTime? from, DateTime? to, Guid? staffUserId, Guid? customerId, Guid? serviceId, int page, int pageSize);

        /// <summary>
        /// Lists a staff member's own appointments — backend-driven search,
        /// price filtering, sorting and pagination (page/pageSize/totalCount/totalPages).
        /// </summary>
        Task<ApiResponse<PaginatedResponse<AppointmentResponse>>> GetStaffAppointmentsAsync(
            Guid staffUserId, string? status, string? search, string? priceFilter, string? sort,
            DateTime? from, DateTime? to, int page, int pageSize);

    /// <summary>
    /// Staff: book an appointment for themselves at their linked business.
    /// StaffUserId and CustomerId are always forced to the caller — a staff
    /// member can never book for (or as) another staff member. The slot must
    /// still be on the bookable grid and lead time is enforced like customers.
    /// </summary>
    Task<ApiResponse<AppointmentResponse>> CreateStaffSelfAppointmentAsync(
        Guid staffUserId, CreateAppointmentRequest request);

    /// <summary>
    /// Staff: a specific customer's appointments at the staff member's
    /// linked business (read-only history for the staff customer detail).
    /// </summary>
    Task<ApiResponse<PaginatedResponse<AppointmentResponse>>> GetStaffCustomerAppointmentsAsync(
        Guid staffUserId, Guid customerId, int page, int pageSize);
}