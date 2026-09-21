using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

public interface IReferralService
{
    // ── Referral Program (Business) ─────────────────────────
    Task<ApiResponse<ReferralProgramResponse>> UpsertProgramAsync(Guid ownerId, UpsertReferralProgramRequest request);
    Task<ApiResponse<ReferralProgramResponse>> GetProgramAsync(Guid businessId);

    // ── Referral Links (Customer) ───────────────────────────
    Task<ApiResponse<ReferralLinkResponse>> GenerateLinkAsync(Guid customerId, GenerateReferralLinkRequest request);
    Task<ApiResponse<List<ReferralLinkResponse>>> GetMyLinksAsync(Guid customerId);
    Task<ApiResponse<ReferralLinkResponse>> GetLinkForBusinessAsync(Guid customerId, Guid businessId);
    Task<ApiResponse<bool>> TrackLinkOpenAsync(string code);

    // ── Referral Resolution (Referee) ───────────────────────
    Task<ApiResponse<ResolveReferralResponse>> ResolveCodeAsync(Guid refereeId, ResolveReferralRequest request);

    // ── Referral Tracking ───────────────────────────────────
    Task<ApiResponse<List<ReferralResponse>>> GetMyReferralsAsync(Guid customerId);
    Task<ApiResponse<List<ReferralResponse>>> GetIncomingReferralsAsync(Guid customerId);
    Task<ApiResponse<ReferralStatsResponse>> GetMyStatsAsync(Guid customerId);

    // ── Business Referral Tracking ──────────────────────────
    Task<ApiResponse<BusinessReferralOverviewResponse>> GetBusinessReferralsAsync(Guid ownerId);

    // ── Internal qualification triggers ─────────────────────

    /// <summary>
    /// Shared qualification routine: turns a waiting referral into a successful
    /// one and issues the referral program's reward. Idempotent — a referral that
    /// is already qualified or rewarded is left untouched. Legacy trigger: the
    /// referee's first stamp.
    /// </summary>
    Task ProcessFirstStampReferralAsync(Guid refereeId, Guid businessId);

    /// <summary>
    /// Appointment-completion trigger (spec §17): a referral only becomes
    /// successful once the referred customer completes their FIRST qualifying
    /// appointment. Publishing the resulting ReferralCompleted fact for
    /// downstream consumers (Loyalty) is owned here, not by the consumer.
    /// </summary>
    Task ProcessAppointmentCompletionAsync(Guid refereeId, Guid businessId, Guid appointmentId);
}
